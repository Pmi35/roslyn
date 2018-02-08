﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Operations
{
    internal sealed class ControlFlowGraphBuilder : OperationVisitor<int?, IOperation>
    {
        // PROTOTYPE(dataflow): does it have to be a field?
        private readonly BasicBlock _entry = new BasicBlock(BasicBlockKind.Entry);

        // PROTOTYPE(dataflow): does it have to be a field?
        private readonly BasicBlock _exit = new BasicBlock(BasicBlockKind.Exit);

        private ArrayBuilder<BasicBlock> _blocks;
        private BasicBlock _currentBasicBlock;
        private IOperation _currentStatement;
        private ArrayBuilder<IOperation> _evalStack;
        private IOperation _currentConditionalAccessInstance;

        // PROTOTYPE(dataflow): does the public API IFlowCaptureOperation.Id specify how identifiers are created or assigned?
        // Should we use uint to exclude negative integers? Should we randomize them in any way to avoid dependencies 
        // being taken?
        private int _availableCaptureId = 0;

        private ControlFlowGraphBuilder()
        { }

        public static ControlFlowGraph Create(IBlockOperation body)
        {
            var builder = new ControlFlowGraphBuilder();
            var blocks = ArrayBuilder<BasicBlock>.GetInstance();
            builder._blocks = blocks;
            builder._evalStack = ArrayBuilder<IOperation>.GetInstance();
            blocks.Add(builder._entry);

            builder.VisitStatement(body);
            builder.AppendNewBlock(builder._exit);

            Pack(blocks);

            Debug.Assert(builder._evalStack.Count == 0);
            builder._evalStack.Free();

            return new ControlFlowGraph(blocks.ToImmutableAndFree());
        }

        /// <summary>
        /// Do a pass to eliminate blocks without statements that can be merged with predecessor(s).
        /// </summary>
        private static void Pack(ArrayBuilder<BasicBlock> blocks)
        {
            int count = blocks.Count - 1;
            for (int i = 1; i < count; i++)
            {
                BasicBlock block = blocks[i];
                Debug.Assert(block.Next != null);
                if (!block.Statements.IsEmpty)
                {
                    continue;
                }

                BasicBlock next = block.Next;

                if (block.Conditional.Condition == null)
                {
                    if (next != block)
                    {
                        foreach (BasicBlock predecessor in block.Predecessors)
                        {
                            if (predecessor.Next == block)
                            {
                                predecessor.Next = next;
                                next.AddPredecessor(predecessor);
                            }

                            (IOperation condition, bool jumpIfTrue, BasicBlock destination) = predecessor.Conditional;
                            if (destination == block)
                            {
                                predecessor.Conditional = (condition, jumpIfTrue, next);
                                next.AddPredecessor(predecessor);
                            }
                        }

                        next.RemovePredecessor(block);

                        blocks.RemoveAt(i);
                        i--;
                        count--;
                    }
                }
                else
                {
                    ImmutableHashSet<BasicBlock> predecessors = block.Predecessors;

                    if (predecessors.Count == 1)
                    {
                        BasicBlock predecessor = predecessors.Single();

                        if (predecessor.Kind != BasicBlockKind.Entry && predecessor.Next == block && predecessor.Conditional.Condition == null)
                        {
                            predecessor.Next = next;
                            next.AddPredecessor(predecessor);
                            next.RemovePredecessor(block);

                            predecessor.Conditional = block.Conditional;
                            next = block.Conditional.Destination;
                            next.AddPredecessor(predecessor);
                            next.RemovePredecessor(block);

                            blocks.RemoveAt(i);
                            i--;
                            count--;
                        }
                    }
                }
            }
        }

        private void VisitStatement(IOperation operation)
        {
            IOperation saveCurrentStatement = _currentStatement;
            _currentStatement = operation;
            Debug.Assert(_evalStack.Count == 0);

            AddStatement(Visit(operation, null));
            Debug.Assert(_evalStack.Count == 0);
            _currentStatement = saveCurrentStatement;
        }

        private BasicBlock CurrentBasicBlock
        {
            get
            {
                if (_currentBasicBlock == null)
                {
                    AppendNewBlock(new BasicBlock(BasicBlockKind.Block));
                }

                return _currentBasicBlock;
            }
        }

        private void AddStatement(
            IOperation statement
#if DEBUG
            , bool spillingTheStack = false
#endif
            )
        {
#if DEBUG
            Debug.Assert(spillingTheStack || !_evalStack.Any(o => o.Kind != OperationKind.FlowCaptureReference));
#endif
            if (statement == null)
            {
                return;
            }

            Operation.SetParentOperation(statement, null);
            CurrentBasicBlock.AddStatement(statement);
        }

        private void AppendNewBlock(BasicBlock block)
        {
            Debug.Assert(block != null);
            BasicBlock prevBlock = _blocks.Last();

            if (prevBlock.Next == null)
            {
                LinkBlocks(prevBlock, block);
            }

            _blocks.Add(block);
            _currentBasicBlock = block;
        }

        private static void LinkBlocks(BasicBlock prevBlock, BasicBlock nextBlock)
        {
            Debug.Assert(prevBlock.Next == null);
            prevBlock.Next = nextBlock;
            nextBlock.AddPredecessor(prevBlock);
        }

        public override IOperation VisitBlock(IBlockOperation operation, int? captureIdForResult)
        {
            foreach (var statement in operation.Operations)
            {
                VisitStatement(statement);
            }

            return null;
        }

        public override IOperation VisitConditional(IConditionalOperation operation, int? captureIdForResult)
        {
            if (operation == _currentStatement)
            {
                if (operation.WhenFalse == null)
                {
                    // if (condition) 
                    //   consequence;  
                    //
                    // becomes
                    //
                    // GotoIfFalse condition afterif;
                    // consequence;
                    // afterif:

                    BasicBlock afterIf = null;
                    VisitConditionalBranch(operation.Condition, ref afterIf, sense: false);
                    VisitStatement(operation.WhenTrue);
                    AppendNewBlock(afterIf);
                }
                else
                {
                    // if (condition)
                    //     consequence;
                    // else 
                    //     alternative
                    //
                    // becomes
                    //
                    // GotoIfFalse condition alt;
                    // consequence
                    // goto afterif;
                    // alt:
                    // alternative;
                    // afterif:

                    BasicBlock whenFalse = null;
                    VisitConditionalBranch(operation.Condition, ref whenFalse, sense: false);

                    VisitStatement(operation.WhenTrue);

                    var afterIf = new BasicBlock(BasicBlockKind.Block);
                    LinkBlocks(CurrentBasicBlock, afterIf);
                    _currentBasicBlock = null;

                    AppendNewBlock(whenFalse);
                    VisitStatement(operation.WhenFalse);

                    AppendNewBlock(afterIf);
                }

                return null;
            }
            else
            {
                // condition ? consequence : alternative
                //
                // becomes
                //
                // GotoIfFalse condition alt;
                // capture = consequence
                // goto afterif;
                // alt:
                // capture = alternative;
                // afterif:
                // result - capture

                SpillEvalStack();
                int captureId = captureIdForResult ?? _availableCaptureId++;

                BasicBlock whenFalse = null;
                VisitConditionalBranch(operation.Condition, ref whenFalse, sense: false);

                VisitAndCapture(operation.WhenTrue, captureId);

                var afterIf = new BasicBlock(BasicBlockKind.Block);
                LinkBlocks(CurrentBasicBlock, afterIf);
                _currentBasicBlock = null;

                AppendNewBlock(whenFalse);

                VisitAndCapture(operation.WhenFalse, captureId);

                AppendNewBlock(afterIf);

                return new FlowCaptureReference(captureId, operation.Syntax, operation.Type, operation.ConstantValue);
            }
        }

        private void VisitAndCapture(IOperation operation, int captureId)
        {
            IOperation result = Visit(operation, captureId);
            CaptureResultIfNotAlready(operation.Syntax, captureId, result);
        }

        private int VisitAndCapture(IOperation operation)
        {
            int saveAvailableCaptureId = _availableCaptureId;
            IOperation rewritten = Visit(operation);

            int captureId;
            if (rewritten.Kind != OperationKind.FlowCaptureReference ||
                saveAvailableCaptureId > (captureId = ((IFlowCaptureReferenceOperation)rewritten).Id))
            {
                captureId = _availableCaptureId++;
                AddStatement(new FlowCapture(captureId, operation.Syntax, rewritten));
            }

            return captureId;
        }

        private void CaptureResultIfNotAlready(SyntaxNode syntax, int captureId, IOperation result)
        {
            if (result.Kind != OperationKind.FlowCaptureReference ||
                captureId != ((IFlowCaptureReferenceOperation)result).Id)
            {
                AddStatement(new FlowCapture(captureId, syntax, result));
            }
        }

        private void SpillEvalStack()
        {
            for (int i = 0; i < _evalStack.Count; i++)
            {
                IOperation operation = _evalStack[i];
                if (operation.Kind != OperationKind.FlowCaptureReference)
                {
                    int captureId = _availableCaptureId++;

                    AddStatement(new FlowCapture(captureId, operation.Syntax, Visit(operation))
#if DEBUG
                                 , spillingTheStack: true
#endif
                                );

                    _evalStack[i] = new FlowCaptureReference(captureId, operation.Syntax, operation.Type, operation.ConstantValue);
                }
            }
        }

        public override IOperation VisitSimpleAssignment(ISimpleAssignmentOperation operation, int? captureIdForResult)
        {
            _evalStack.Push(Visit(operation.Target));
            IOperation value = Visit(operation.Value);
            return new SimpleAssignmentExpression(_evalStack.Pop(), operation.IsRef, value, null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitCompoundAssignment(ICompoundAssignmentOperation operation, int? captureIdForResult)
        {
            var compoundAssignment = (BaseCompoundAssignmentExpression)operation;
            _evalStack.Push(Visit(compoundAssignment.Target));
            IOperation value = Visit(compoundAssignment.Value);

            return new CompoundAssignmentOperation(_evalStack.Pop(), value, compoundAssignment.InConversionConvertible, compoundAssignment.OutConversionConvertible, operation.OperatorKind, operation.IsLifted, operation.IsChecked, operation.OperatorMethod, semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        // PROTOTYPE(dataflow):
        //public override IOperation VisitArrayElementReference(IArrayElementReferenceOperation operation, int? captureIdForResult)
        //{
        //    _evalStack.Push(Visit(operation.ArrayReference));
        //    foreach (var index in operation.Indices)
        //    return new ArrayElementReferenceExpression(Visit(operation.ArrayReference), VisitArray(operation.Indices), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        //}

        private static bool IsConditional(IBinaryOperation operation)
        {
            switch (operation.OperatorKind)
            {
                case BinaryOperatorKind.ConditionalOr:
                case BinaryOperatorKind.ConditionalAnd:
                    return true;
            }

            return false;
        }

        public override IOperation VisitBinaryOperator(IBinaryOperation operation, int? captureIdForResult)
        {
            if (IsConditional(operation))
            {
                return VisitBinaryConditionalOperator(operation, sense: true, captureIdForResult, fallToTrueOpt: null, fallToFalseOpt: null);
            }

            _evalStack.Push(Visit(operation.LeftOperand));
            IOperation rightOperand = Visit(operation.RightOperand);
            return new BinaryOperatorExpression(operation.OperatorKind, _evalStack.Pop(), rightOperand, operation.IsLifted, operation.IsChecked, operation.IsCompareText, operation.OperatorMethod, semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitUnaryOperator(IUnaryOperation operation, int? captureIdForResult)
        {
            // PROTOTYPE(dataflow): ensure we properly detect logical Not
            if (operation.OperatorKind == UnaryOperatorKind.Not)
            {
                return VisitConditionalExpression(operation.Operand, sense: false, captureIdForResult, fallToTrueOpt: null, fallToFalseOpt: null);
            }

            return base.VisitUnaryOperator(operation, captureIdForResult);
        }

        private IOperation VisitBinaryConditionalOperator(IBinaryOperation binOp, bool sense, int? captureIdForResult,
                                                          BasicBlock fallToTrueOpt, BasicBlock fallToFalseOpt)
        {
            bool andOrSense = sense;

            switch (binOp.OperatorKind)
            {
                case BinaryOperatorKind.ConditionalOr:
                    Debug.Assert(binOp.LeftOperand.Type.SpecialType == SpecialType.System_Boolean);
                    Debug.Assert(binOp.RightOperand.Type.SpecialType == SpecialType.System_Boolean);

                    // Rewrite (a || b) as ~(~a && ~b)
                    andOrSense = !andOrSense;
                    // Fall through
                    goto case BinaryOperatorKind.ConditionalAnd;

                case BinaryOperatorKind.ConditionalAnd:
                    Debug.Assert(binOp.LeftOperand.Type.SpecialType == SpecialType.System_Boolean);
                    Debug.Assert(binOp.RightOperand.Type.SpecialType == SpecialType.System_Boolean);

                    // ~(a && b) is equivalent to (~a || ~b)
                    if (!andOrSense)
                    {
                        // generate (~a || ~b)
                        return VisitShortCircuitingOperator(binOp, sense: sense, stopSense: sense, stopValue: true, captureIdForResult, fallToTrueOpt, fallToFalseOpt);
                    }
                    else
                    {
                        // generate (a && b)
                        return VisitShortCircuitingOperator(binOp, sense: sense, stopSense: !sense, stopValue: false, captureIdForResult, fallToTrueOpt, fallToFalseOpt);
                    }

                default:
                    throw ExceptionUtilities.UnexpectedValue(binOp.OperatorKind);
            }
        }

        private IOperation VisitShortCircuitingOperator(IBinaryOperation condition, bool sense, bool stopSense, bool stopValue,
                                                        int? captureIdForResult, BasicBlock fallToTrueOpt, BasicBlock fallToFalseOpt)
        {
            // we generate:
            //
            // gotoif (a == stopSense) fallThrough
            // b == sense
            // goto labEnd
            // fallThrough:
            // stopValue
            // labEnd:
            //                 AND       OR
            //            +-  ------    -----
            // stopSense  |   !sense    sense
            // stopValue  |     0         1

            SpillEvalStack();
            int captureId = captureIdForResult ?? _availableCaptureId++;

            ref BasicBlock lazyFallThrough = ref stopSense ? ref fallToTrueOpt : ref fallToFalseOpt;
            bool newFallThroughBlock = (lazyFallThrough == null);

            VisitConditionalBranch(condition.LeftOperand, ref lazyFallThrough, stopSense);

            IOperation resultFromRight = VisitConditionalExpression(condition.RightOperand, sense, captureId, fallToTrueOpt, fallToFalseOpt);

            CaptureResultIfNotAlready(condition.RightOperand.Syntax, captureId, resultFromRight);

            if (newFallThroughBlock)
            {
                var labEnd = new BasicBlock(BasicBlockKind.Block);
                LinkBlocks(CurrentBasicBlock, labEnd);
                _currentBasicBlock = null;

                AppendNewBlock(lazyFallThrough);

                var constantValue = new Optional<object>(stopValue);
                SyntaxNode leftSyntax = (lazyFallThrough.Predecessors.Count == 1 ? condition.LeftOperand : condition).Syntax;
                AddStatement(new FlowCapture(captureId, leftSyntax, new LiteralExpression(semanticModel: null, leftSyntax, condition.Type, constantValue, isImplicit: true)));

                AppendNewBlock(labEnd);
            }

            return new FlowCaptureReference(captureId, condition.Syntax, condition.Type, condition.ConstantValue);
        }

        private IOperation VisitConditionalExpression(IOperation condition, bool sense, int? captureIdForResult, BasicBlock fallToTrueOpt, BasicBlock fallToFalseOpt)
        {
            // PROTOTYPE(dataflow): Do not erase UnaryOperatorKind.Not if ProduceIsSense below will have to add it back.
            while (condition.Kind == OperationKind.UnaryOperator)
            {
                var unOp = (IUnaryOperation)condition;
                // PROTOTYPE(dataflow): ensure we properly detect logical Not
                if (unOp.OperatorKind != UnaryOperatorKind.Not)
                {
                    break;
                }
                condition = unOp.Operand;
                sense = !sense;
            }

            Debug.Assert(condition.Type.SpecialType == SpecialType.System_Boolean);

            if (condition.Kind == OperationKind.BinaryOperator)
            {
                var binOp = (IBinaryOperation)condition;
                if (IsConditional(binOp))
                {
                    return VisitBinaryConditionalOperator(binOp, sense, captureIdForResult, fallToTrueOpt, fallToFalseOpt);
                }
            }

            return ProduceIsSense(Visit(condition), sense);
        }

        private IOperation ProduceIsSense(IOperation condition, bool sense)
        {
            if (!sense)
            {
                return new UnaryOperatorExpression(UnaryOperatorKind.Not,
                                                   condition,
                                                   isLifted: false, // PROTOTYPE(dataflow): Deal with nullable
                                                   isChecked: false,
                                                   operatorMethod: null,
                                                   semanticModel: null,
                                                   condition.Syntax,
                                                   condition.Type,
                                                   constantValue: default, // revert constant value if we have one.
                                                   isImplicit: true);
            }

            return condition;
        }

        private void VisitConditionalBranch(IOperation condition, ref BasicBlock dest, bool sense)
        {
            oneMoreTime:

            switch (condition.Kind)
            {
                case OperationKind.BinaryOperator:
                    var binOp = (IBinaryOperation)condition;
                    bool testBothArgs = sense;

                    switch (binOp.OperatorKind)
                    {
                        case BinaryOperatorKind.ConditionalOr:
                            testBothArgs = !testBothArgs;
                            // Fall through
                            goto case BinaryOperatorKind.ConditionalAnd;

                        case BinaryOperatorKind.ConditionalAnd:
                            if (testBothArgs)
                            {
                                // gotoif(a != sense) fallThrough
                                // gotoif(b == sense) dest
                                // fallThrough:

                                BasicBlock fallThrough = null;

                                VisitConditionalBranch(binOp.LeftOperand, ref fallThrough, !sense);
                                VisitConditionalBranch(binOp.RightOperand, ref dest, sense);

                                if (fallThrough != null)
                                {
                                    AppendNewBlock(fallThrough);
                                }
                            }
                            else
                            {
                                // gotoif(a == sense) labDest
                                // gotoif(b == sense) labDest

                                VisitConditionalBranch(binOp.LeftOperand, ref dest, sense);
                                condition = binOp.RightOperand;
                                goto oneMoreTime;
                            }
                            return;
                    }

                    // none of above. 
                    // then it is regular binary expression - Or, And, Xor ...
                    goto default;

                case OperationKind.UnaryOperator:
                    var unOp = (IUnaryOperation)condition;
                    if (unOp.OperatorKind == UnaryOperatorKind.Not && unOp.Operand.Type.SpecialType == SpecialType.System_Boolean)
                    {
                        sense = !sense;
                        condition = unOp.Operand;
                        goto oneMoreTime;
                    }
                    goto default;

                default:
                    condition = Operation.SetParentOperation(Visit(condition), null);
                    dest = dest ?? new BasicBlock(BasicBlockKind.Block);
                    LinkBlocks(CurrentBasicBlock, (condition, sense, dest));
                    _currentBasicBlock = null;
                    return;
            }
        }

        private static void LinkBlocks(BasicBlock previous, (IOperation Condition, bool JumpIfTrue, BasicBlock Destination) next)
        {
            Debug.Assert(previous.Conditional.Condition == null);
            Debug.Assert(next.Condition != null);
            Debug.Assert(next.Condition.Parent == null);
            next.Destination.AddPredecessor(previous);
            previous.Conditional = next;
        }

        public override IOperation VisitCoalesce(ICoalesceOperation operation, int? captureIdForResult)
        {
            SyntaxNode valueSyntax = operation.Value.Syntax;
            ITypeSymbol valueTypeOpt = operation.Value.Type;

            SpillEvalStack();
            int testExpressionCaptureId = VisitAndCapture(operation.Value);

            var whenNull = new BasicBlock(BasicBlockKind.Block);
            Optional<object> constantValue = operation.Value.ConstantValue;

            LinkBlocks(CurrentBasicBlock,
                       (Operation.SetParentOperation(MakeIsNullOperation(((Operation)operation).SemanticModel,
                                                                         new FlowCaptureReference(testExpressionCaptureId, valueSyntax, valueTypeOpt, constantValue)),
                                                     null),
                        true,
                        whenNull));
            _currentBasicBlock = null;

            CommonConversion testConversion = operation.ValueConversion;
            var capturedValue = new FlowCaptureReference(testExpressionCaptureId, valueSyntax, valueTypeOpt, constantValue);
            IOperation convertedTestExpression = null;

            if (testConversion.Exists)
            {
                IOperation possiblyUnwrappedValue;

                if (ITypeSymbolHelpers.IsNullableType(valueTypeOpt) &&
                    (!testConversion.IsIdentity || !ITypeSymbolHelpers.IsNullableType(operation.Type)))
                {
                    possiblyUnwrappedValue = TryUnwrapNullableValue(capturedValue);
                    // PROTOTYPE(dataflow): The scenario with missing GetValueOrDefault is not covered by unit-tests.
                }
                else
                {
                    possiblyUnwrappedValue = capturedValue;
                }

                if (possiblyUnwrappedValue != null)
                {
                    if (testConversion.IsIdentity)
                    {
                        convertedTestExpression = possiblyUnwrappedValue;
                    }
                    else
                    {
                        convertedTestExpression = new ConversionOperation(possiblyUnwrappedValue, ((BaseCoalesceExpression)operation).ConvertibleValueConversion,
                                                                          isTryCast: false, isChecked: false, semanticModel: null, valueSyntax, operation.Type,
                                                                          constantValue: default, isImplicit: true);
                    }
                }
            }

            if (convertedTestExpression == null)
            {
                convertedTestExpression = MakeInvalidOperation(operation.Type, capturedValue);
            }

            int resultCaptureId = captureIdForResult ?? _availableCaptureId++;

            AddStatement(new FlowCapture(resultCaptureId, valueSyntax, convertedTestExpression));

            var afterCoalesce = new BasicBlock(BasicBlockKind.Block);
            LinkBlocks(CurrentBasicBlock, afterCoalesce);
            _currentBasicBlock = null;

            AppendNewBlock(whenNull);

            VisitAndCapture(operation.WhenNull, resultCaptureId);

            AppendNewBlock(afterCoalesce);

            return new FlowCaptureReference(resultCaptureId, operation.Syntax, operation.Type, operation.ConstantValue);
        }

        private static IOperation MakeInvalidOperation(ITypeSymbol type, IOperation child)
        {
            return new InvalidOperation(ImmutableArray.Create<IOperation>(child),
                                        semanticModel: null, child.Syntax, type,
                                        constantValue: default, isImplicit: true);
        }

        private static IsNullOperation MakeIsNullOperation(SemanticModel semanticModel, IOperation operand)
        {
            Optional<object> constantValue = operand.ConstantValue;
            return new IsNullOperation(operand.Syntax, operand,
                                       semanticModel.Compilation.GetSpecialType(SpecialType.System_Boolean),
                                       constantValue.HasValue ? new Optional<object>(constantValue.Value == null) : default);
        }

        private static IOperation TryUnwrapNullableValue(IOperation value)
        {
            ITypeSymbol valueType = value.Type;

            Debug.Assert(ITypeSymbolHelpers.IsNullableType(valueType));

            foreach (ISymbol candidate in valueType.GetMembers("GetValueOrDefault"))
            {
                if (candidate.Kind == SymbolKind.Method && !candidate.IsStatic && candidate.DeclaredAccessibility == Accessibility.Public)
                {
                    var method = (IMethodSymbol)candidate;
                    if (method.Parameters.Length == 0 && !method.ReturnsByRef && !method.ReturnsByRefReadonly &&
                        method.OriginalDefinition.ReturnType.Equals(((INamedTypeSymbol)valueType).OriginalDefinition.TypeParameters[0]))
                    {
                        return new InvocationExpression(method, value, isVirtual: false,
                                                        ImmutableArray<IArgumentOperation>.Empty, semanticModel: null, value.Syntax,
                                                        method.ReturnType, constantValue: default, isImplicit: true);
                    }
                }
            }

            return null;
        }

        public override IOperation VisitConditionalAccess(IConditionalAccessOperation operation, int? captureIdForResult)
        {
            // PROTOTYPE(dataflow): Consider to avoid nullable wrap/unwrap operations by merging conditional access
            //                      with containing:
            //                      - binary operator
            //                      - coalesce expression
            //                      - nullable conversion
            //                      - etc. see references to UpdateConditionalAccess in local rewriter

            SpillEvalStack();

            var whenNull = new BasicBlock(BasicBlockKind.Block);

            IConditionalAccessOperation currentConditionalAccess = operation;
            IOperation testExpression;

            while (true)
            {
                testExpression = currentConditionalAccess.Operation;
                SyntaxNode testExpressionSyntax = testExpression.Syntax;
                ITypeSymbol testExpressionType = testExpression.Type;

                int testExpressionCaptureId = VisitAndCapture(testExpression);
                Optional<object> constantValue = testExpression.ConstantValue;

                LinkBlocks(CurrentBasicBlock,
                           (Operation.SetParentOperation(MakeIsNullOperation(((Operation)operation).SemanticModel,
                                                                             new FlowCaptureReference(testExpressionCaptureId, testExpressionSyntax, testExpressionType, constantValue)), 
                                                         null),
                            true, 
                            whenNull));
                _currentBasicBlock = null;

                IOperation receiver = new FlowCaptureReference(testExpressionCaptureId, testExpressionSyntax, testExpressionType, constantValue);

                if (ITypeSymbolHelpers.IsNullableType(testExpressionType))
                {
                    receiver = TryUnwrapNullableValue(receiver) ??
                               // PROTOTYPE(dataflow): The scenario with missing GetValueOrDefault is not covered by unit-tests.
                               MakeInvalidOperation(((INamedTypeSymbol)testExpressionType).TypeArguments[0], receiver);
                }

                Debug.Assert(_currentConditionalAccessInstance == null);
                _currentConditionalAccessInstance = receiver;

                if (currentConditionalAccess.WhenNotNull.Kind != OperationKind.ConditionalAccess)
                {
                    break;
                }

                currentConditionalAccess = (IConditionalAccessOperation)currentConditionalAccess.WhenNotNull;
            }

            int resultCaptureId = captureIdForResult ?? _availableCaptureId++;

            if (ITypeSymbolHelpers.IsNullableType(operation.Type) && !ITypeSymbolHelpers.IsNullableType(currentConditionalAccess.WhenNotNull.Type))
            {
                IOperation access = Visit(currentConditionalAccess.WhenNotNull);
                AddStatement(new FlowCapture(resultCaptureId, currentConditionalAccess.WhenNotNull.Syntax,
                                             TryMakeNullableValue((INamedTypeSymbol)operation.Type, access) ??
                                             // PROTOTYPE(dataflow): The scenario with missing constructor is not covered by unit-tests.
                                             MakeInvalidOperation(operation.Type, access)));
            }
            else
            {
                VisitAndCapture(currentConditionalAccess.WhenNotNull, resultCaptureId);
            }

            Debug.Assert(_currentConditionalAccessInstance == null);

            var afterAccess = new BasicBlock(BasicBlockKind.Block);
            LinkBlocks(CurrentBasicBlock, afterAccess);
            _currentBasicBlock = null;

            AppendNewBlock(whenNull);

            SyntaxNode defaultValueSyntax = (operation.Operation == testExpression ? testExpression : operation).Syntax;

            AddStatement(new FlowCapture(resultCaptureId,
                                         defaultValueSyntax,
                                         new DefaultValueExpression(semanticModel: null, defaultValueSyntax, operation.Type,
                                                                    (operation.Type.IsReferenceType && !ITypeSymbolHelpers.IsNullableType(operation.Type)) ?
                                                                        new Optional<object>(null) : default,
                                                                    isImplicit: true)));

            AppendNewBlock(afterAccess);

            return new FlowCaptureReference(resultCaptureId, operation.Syntax, operation.Type, operation.ConstantValue);
        }

        private static IOperation TryMakeNullableValue(INamedTypeSymbol type, IOperation underlyingValue)
        {
            Debug.Assert(ITypeSymbolHelpers.IsNullableType(type));

            foreach (IMethodSymbol method in type.InstanceConstructors)
            {
                if (method.DeclaredAccessibility == Accessibility.Public && method.Parameters.Length == 1 &&
                    method.OriginalDefinition.Parameters[0].Type.Equals(type.OriginalDefinition.TypeParameters[0]))
                {
                    return new ObjectCreationExpression(method, initializer: null,
                                                        ImmutableArray.Create<IArgumentOperation>(
                                                                    new ArgumentOperation(underlyingValue,
                                                                                        ArgumentKind.Explicit,
                                                                                        method.Parameters[0],
                                                                                        inConversionOpt: null,
                                                                                        outConversionOpt: null,
                                                                                        semanticModel: null,
                                                                                        underlyingValue.Syntax,
                                                                                        constantValue: null,
                                                                                        isImplicit: true)),
                                                        semanticModel: null,
                                                        underlyingValue.Syntax,
                                                        type,
                                                        constantValue: null,
                                                        isImplicit: true);
                }
            }

            return null;
        }

        public override IOperation VisitConditionalAccessInstance(IConditionalAccessInstanceOperation operation, int? captureIdForResult)
        {
            Debug.Assert(_currentConditionalAccessInstance != null);
            IOperation result = _currentConditionalAccessInstance;
            _currentConditionalAccessInstance = null;
            return result;
        }

        public override IOperation VisitWhileLoop(IWhileLoopOperation operation, int? captureIdForResult)
        {
            Debug.Assert(_currentStatement == operation);

            // while (condition) 
            //   body;
            //
            // becomes
            //
            // goto continue;
            // start: 
            // {
            //     body
            //     continue:
            //     GotoIfTrue condition start;
            // }
            // break:

            // do
            //   body
            // while (condition);
            //
            // becomes
            //
            // start: 
            // {
            //   body
            //   continue:
            //   GotoIfTrue condition start;
            // }
            // break:

            BasicBlock beforeLoop = CurrentBasicBlock;

            var start = new BasicBlock(BasicBlockKind.Block);
            var @continue = new BasicBlock(BasicBlockKind.Block);
            var @break = new BasicBlock(BasicBlockKind.Block);

            if (operation.ConditionIsTop)
            {
                LinkBlocks(CurrentBasicBlock, @continue);
            }
            else
            {
                LinkBlocks(CurrentBasicBlock, start);
            }

            AppendNewBlock(start);
            VisitStatement(operation.Body);

            AppendNewBlock(@continue);

            VisitConditionalBranch(operation.Condition, ref start, sense: operation.ConditionIsUntil ? false : true);

            AppendNewBlock(@break);

            return null;
        }

        public override IOperation VisitVariableDeclarationGroup(IVariableDeclarationGroupOperation operation, int? captureIdForResult)
        {
            // Anything that has a declaration group (such as for loops) needs to handle them directly itself,
            // this should only be encountered by the visitor for declaration statements.
            Debug.Assert(_currentStatement == operation);

            // We erase variable declarations from the control flow graph, as variable lifetime information is
            // contained in a parallel data structure.
            foreach (var declaration in operation.Declarations)
            {
                HandleVariableDeclaration(declaration);
            }

            return null;
        }

        private void HandleVariableDeclaration(IVariableDeclarationOperation operation)
        {
            foreach (IVariableDeclaratorOperation declarator in operation.Declarators)
            {
                ILocalSymbol localSymbol = declarator.Symbol;

                IOperation initializer = null;
                SyntaxNode assignmentSyntax = null;
                if (declarator.Initializer != null)
                {
                    initializer = Visit(declarator.Initializer.Value);
                    assignmentSyntax = declarator.Syntax;
                }

                if (operation.Initializer != null)
                {
                    IOperation operationInitializer = Visit(operation.Initializer.Value);
                    assignmentSyntax = operation.Syntax;
                    if (initializer != null)
                    {
                        // PROTOTYPE(dataflow): Add a test with control flow in a shared initializer after
                        // object creation support has been added
                        initializer = new InvalidOperation(ImmutableArray.Create(initializer, operationInitializer),
                                                           semanticModel: null,
                                                           operation.Syntax,
                                                           type: localSymbol.Type,
                                                           constantValue: default,
                                                           isImplicit: true);
                    }
                    else
                    {
                        initializer = operationInitializer;
                    }
                }

                if (initializer != null)
                {
                    // We can't use the IdentifierToken as the syntax for the local reference, so we use the
                    // entire declarator as the node
                    var localRef = new LocalReferenceExpression(localSymbol, isDeclaration: true, semanticModel: null, declarator.Syntax, localSymbol.Type, constantValue: default, isImplicit: true);
                    // PROTOTYPE(dataflow): We'd like to remove ExpressionStatements from the CFG altogether,
                    // as they're useless when all you need to do is look to see if the parent is null
                    var assignment = new SimpleAssignmentExpression(localRef, isRef: localSymbol.IsRef, initializer, semanticModel: null, assignmentSyntax, localRef.Type, constantValue: default, isImplicit: true);
                    var statement = new ExpressionStatement(assignment, semanticModel: null, assignmentSyntax, type: null, constantValue: default, isImplicit: true);
                    AddStatement(statement);
                }
            }
        }

        public override IOperation VisitVariableDeclaration(IVariableDeclarationOperation operation, int? captureIdForResult)
        {
            // All variable declarators should be handled by VisitVariableDeclarationGroup.
            throw ExceptionUtilities.Unreachable;
        }

        public override IOperation VisitVariableDeclarator(IVariableDeclaratorOperation operation, int? captureIdForResult)
        {
            // All variable declarators should be handled by VisitVariableDeclaration.
            throw ExceptionUtilities.Unreachable;
        }

        public override IOperation VisitVariableInitializer(IVariableInitializerOperation operation, int? captureIdForResult)
        {
            // All variable initializers should be removed from the tree by VisitVariableDeclaration.
            throw ExceptionUtilities.Unreachable;
        }

        private T Visit<T>(T node) where T : IOperation
        {
            return (T)Visit(node, argument: null);
        }

        public IOperation Visit(IOperation operation)
        {
            return Visit(operation, argument: null);
        }

        public override IOperation DefaultVisit(IOperation operation, int? captureIdForResult)
        {
            // this should never reach, otherwise, there is missing override for IOperation type
            throw ExceptionUtilities.Unreachable;
        }

        #region PROTOTYPE(dataflow): Naive implementation that simply clones nodes and erases SemanticModel, likely to change
        internal override IOperation VisitNoneOperation(IOperation operation, int? captureIdForResult)
        {
            return Operation.CreateOperationNone(semanticModel: null, operation.Syntax, operation.ConstantValue, () => VisitArray(operation.Children.ToImmutableArray()), operation.IsImplicit);
        }

        private ImmutableArray<T> VisitArray<T>(ImmutableArray<T> nodes) where T : IOperation
        {
            // clone the array
            return nodes.SelectAsArray(n => Visit(n));
        }

        public override IOperation VisitConversion(IConversionOperation operation, int? captureIdForResult)
        {
            return new ConversionOperation(Visit(operation.Operand), ((BaseConversionExpression)operation).ConvertibleConversion, operation.IsTryCast, operation.IsChecked, semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitSwitch(ISwitchOperation operation, int? captureIdForResult)
        {
            return new SwitchStatement(Visit(operation.Value), VisitArray(operation.Cases), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitSwitchCase(ISwitchCaseOperation operation, int? captureIdForResult)
        {
            return new SwitchCase(VisitArray(operation.Clauses), VisitArray(operation.Body), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitSingleValueCaseClause(ISingleValueCaseClauseOperation operation, int? captureIdForResult)
        {
            return new SingleValueCaseClause(Visit(operation.Value), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitRelationalCaseClause(IRelationalCaseClauseOperation operation, int? captureIdForResult)
        {
            return new RelationalCaseClause(Visit(operation.Value), operation.Relation, semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitRangeCaseClause(IRangeCaseClauseOperation operation, int? captureIdForResult)
        {
            return new RangeCaseClause(Visit(operation.MinimumValue), Visit(operation.MaximumValue), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitDefaultCaseClause(IDefaultCaseClauseOperation operation, int? captureIdForResult)
        {
            return new DefaultCaseClause(semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitForLoop(IForLoopOperation operation, int? captureIdForResult)
        {
            return new ForLoopStatement(VisitArray(operation.Before), Visit(operation.Condition), VisitArray(operation.AtLoopBottom), operation.Locals, Visit(operation.Body), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitForToLoop(IForToLoopOperation operation, int? captureIdForResult)
        {
            return new ForToLoopStatement(operation.Locals, Visit(operation.LoopControlVariable), Visit(operation.InitialValue), Visit(operation.LimitValue), Visit(operation.StepValue), Visit(operation.Body), VisitArray(operation.NextVariables), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitForEachLoop(IForEachLoopOperation operation, int? captureIdForResult)
        {
            // PROTOTYPE(dataflow): note that the loop control variable can be an IVariableDeclarator directly, and this function is expected to handle it without calling Visit(IVariableDeclarator)
            return new ForEachLoopStatement(operation.Locals, Visit(operation.LoopControlVariable), Visit(operation.Collection), VisitArray(operation.NextVariables), Visit(operation.Body), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitLabeled(ILabeledOperation operation, int? captureIdForResult)
        {
            return new LabeledStatement(operation.Label, Visit(operation.Operation), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitBranch(IBranchOperation operation, int? captureIdForResult)
        {
            return new BranchStatement(operation.Target, operation.BranchKind, semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitEmpty(IEmptyOperation operation, int? captureIdForResult)
        {
            return new EmptyStatement(semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitReturn(IReturnOperation operation, int? captureIdForResult)
        {
            return new ReturnStatement(operation.Kind, Visit(operation.ReturnedValue), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitLock(ILockOperation operation, int? captureIdForResult)
        {
            return new LockStatement(Visit(operation.LockedValue), Visit(operation.Body), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitTry(ITryOperation operation, int? captureIdForResult)
        {
            return new TryStatement(Visit(operation.Body), VisitArray(operation.Catches), Visit(operation.Finally), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitCatchClause(ICatchClauseOperation operation, int? captureIdForResult)
        {
            return new CatchClause(Visit(operation.ExceptionDeclarationOrExpression), operation.ExceptionType, operation.Locals, Visit(operation.Filter), Visit(operation.Handler), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitUsing(IUsingOperation operation, int? captureIdForResult)
        {
            return new UsingStatement(Visit(operation.Resources), Visit(operation.Body), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        internal override IOperation VisitFixed(IFixedOperation operation, int? captureIdForResult)
        {
            return new FixedStatement(Visit(operation.Variables), Visit(operation.Body), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitExpressionStatement(IExpressionStatementOperation operation, int? captureIdForResult)
        {
            return new ExpressionStatement(Visit(operation.Operation), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        internal override IOperation VisitWith(IWithOperation operation, int? captureIdForResult)
        {
            return new WithStatement(Visit(operation.Body), Visit(operation.Value), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitStop(IStopOperation operation, int? captureIdForResult)
        {
            return new StopStatement(semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitEnd(IEndOperation operation, int? captureIdForResult)
        {
            return new EndStatement(semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitInvocation(IInvocationOperation operation, int? captureIdForResult)
        {
            return new InvocationExpression(operation.TargetMethod, Visit(operation.Instance), operation.IsVirtual, VisitArray(operation.Arguments), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitArgument(IArgumentOperation operation, int? captureIdForResult)
        {
            var baseArgument = (BaseArgument)operation;
            return new ArgumentOperation(Visit(operation.Value), operation.ArgumentKind, operation.Parameter, baseArgument.InConversionConvertibleOpt, baseArgument.OutConversionConvertibleOpt, semanticModel: null, operation.Syntax, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitOmittedArgument(IOmittedArgumentOperation operation, int? captureIdForResult)
        {
            return new OmittedArgumentExpression(semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitArrayElementReference(IArrayElementReferenceOperation operation, int? captureIdForResult)
        {
            return new ArrayElementReferenceExpression(Visit(operation.ArrayReference), VisitArray(operation.Indices), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        internal override IOperation VisitPointerIndirectionReference(IPointerIndirectionReferenceOperation operation, int? captureIdForResult)
        {
            return new PointerIndirectionReferenceExpression(Visit(operation.Pointer), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitLocalReference(ILocalReferenceOperation operation, int? captureIdForResult)
        {
            return new LocalReferenceExpression(operation.Local, operation.IsDeclaration, semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitParameterReference(IParameterReferenceOperation operation, int? captureIdForResult)
        {
            return new ParameterReferenceExpression(operation.Parameter, semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitInstanceReference(IInstanceReferenceOperation operation, int? captureIdForResult)
        {
            return new InstanceReferenceExpression(semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitFieldReference(IFieldReferenceOperation operation, int? captureIdForResult)
        {
            return new FieldReferenceExpression(operation.Field, operation.IsDeclaration, Visit(operation.Instance), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitMethodReference(IMethodReferenceOperation operation, int? captureIdForResult)
        {
            return new MethodReferenceExpression(operation.Method, operation.IsVirtual, Visit(operation.Instance), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitPropertyReference(IPropertyReferenceOperation operation, int? captureIdForResult)
        {
            return new PropertyReferenceExpression(operation.Property, Visit(operation.Instance), VisitArray(operation.Arguments), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitEventReference(IEventReferenceOperation operation, int? captureIdForResult)
        {
            return new EventReferenceExpression(operation.Event, Visit(operation.Instance), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitEventAssignment(IEventAssignmentOperation operation, int? captureIdForResult)
        {
            return new EventAssignmentOperation(Visit(operation.EventReference), Visit(operation.HandlerValue), operation.Adds, semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        internal override IOperation VisitPlaceholder(IPlaceholderOperation operation, int? captureIdForResult)
        {
            return new PlaceholderExpression(semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitIsType(IIsTypeOperation operation, int? captureIdForResult)
        {
            return new IsTypeExpression(Visit(operation.ValueOperand), operation.TypeOperand, operation.IsNegated, semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitSizeOf(ISizeOfOperation operation, int? captureIdForResult)
        {
            return new SizeOfExpression(operation.TypeOperand, semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitTypeOf(ITypeOfOperation operation, int? captureIdForResult)
        {
            return new TypeOfExpression(operation.TypeOperand, semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitAnonymousFunction(IAnonymousFunctionOperation operation, int? captureIdForResult)
        {
            return new AnonymousFunctionExpression(operation.Symbol, Visit(operation.Body), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitDelegateCreation(IDelegateCreationOperation operation, int? captureIdForResult)
        {
            return new DelegateCreationExpression(Visit(operation.Target), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitLiteral(ILiteralOperation operation, int? captureIdForResult)
        {
            return new LiteralExpression(semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitAwait(IAwaitOperation operation, int? captureIdForResult)
        {
            return new AwaitExpression(Visit(operation.Operation), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitNameOf(INameOfOperation operation, int? captureIdForResult)
        {
            return new NameOfExpression(Visit(operation.Argument), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitThrow(IThrowOperation operation, int? captureIdForResult)
        {
            return new ThrowExpression(Visit(operation.Exception), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitAddressOf(IAddressOfOperation operation, int? captureIdForResult)
        {
            return new AddressOfExpression(Visit(operation.Reference), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitObjectCreation(IObjectCreationOperation operation, int? captureIdForResult)
        {
            return new ObjectCreationExpression(operation.Constructor, Visit(operation.Initializer), VisitArray(operation.Arguments), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitAnonymousObjectCreation(IAnonymousObjectCreationOperation operation, int? captureIdForResult)
        {
            return new AnonymousObjectCreationExpression(VisitArray(operation.Initializers), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitObjectOrCollectionInitializer(IObjectOrCollectionInitializerOperation operation, int? captureIdForResult)
        {
            return new ObjectOrCollectionInitializerExpression(VisitArray(operation.Initializers), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitMemberInitializer(IMemberInitializerOperation operation, int? captureIdForResult)
        {
            return new MemberInitializerExpression(Visit(operation.InitializedMember), Visit(operation.Initializer), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitCollectionElementInitializer(ICollectionElementInitializerOperation operation, int? captureIdForResult)
        {
            return new CollectionElementInitializerExpression(operation.AddMethod, operation.IsDynamic, VisitArray(operation.Arguments), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitFieldInitializer(IFieldInitializerOperation operation, int? captureIdForResult)
        {
            return new FieldInitializer(operation.InitializedFields, Visit(operation.Value), operation.Kind, semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitPropertyInitializer(IPropertyInitializerOperation operation, int? captureIdForResult)
        {
            return new PropertyInitializer(operation.InitializedProperties, Visit(operation.Value), operation.Kind, semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitParameterInitializer(IParameterInitializerOperation operation, int? captureIdForResult)
        {
            return new ParameterInitializer(operation.Parameter, Visit(operation.Value), operation.Kind, semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitArrayCreation(IArrayCreationOperation operation, int? captureIdForResult)
        {
            return new ArrayCreationExpression(VisitArray(operation.DimensionSizes), Visit(operation.Initializer), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitArrayInitializer(IArrayInitializerOperation operation, int? captureIdForResult)
        {
            return new ArrayInitializer(VisitArray(operation.ElementValues), semanticModel: null, operation.Syntax, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitDeconstructionAssignment(IDeconstructionAssignmentOperation operation, int? captureIdForResult)
        {
            return new DeconstructionAssignmentExpression(Visit(operation.Target), Visit(operation.Value), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitDeclarationExpression(IDeclarationExpressionOperation operation, int? captureIdForResult)
        {
            return new DeclarationExpression(Visit(operation.Expression), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitIncrementOrDecrement(IIncrementOrDecrementOperation operation, int? captureIdForResult)
        {
            bool isDecrement = operation.Kind == OperationKind.Decrement;
            return new IncrementExpression(isDecrement, operation.IsPostfix, operation.IsLifted, operation.IsChecked, Visit(operation.Target), operation.OperatorMethod, semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitParenthesized(IParenthesizedOperation operation, int? captureIdForResult)
        {
            return new ParenthesizedExpression(Visit(operation.Operand), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitDynamicMemberReference(IDynamicMemberReferenceOperation operation, int? captureIdForResult)
        {
            return new DynamicMemberReferenceExpression(Visit(operation.Instance), operation.MemberName, operation.TypeArguments, operation.ContainingType, semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitDynamicObjectCreation(IDynamicObjectCreationOperation operation, int? captureIdForResult)
        {
            return new DynamicObjectCreationExpression(VisitArray(operation.Arguments), ((HasDynamicArgumentsExpression)operation).ArgumentNames, ((HasDynamicArgumentsExpression)operation).ArgumentRefKinds, Visit(operation.Initializer), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitDynamicInvocation(IDynamicInvocationOperation operation, int? captureIdForResult)
        {
            return new DynamicInvocationExpression(Visit(operation.Operation), VisitArray(operation.Arguments), ((HasDynamicArgumentsExpression)operation).ArgumentNames, ((HasDynamicArgumentsExpression)operation).ArgumentRefKinds, semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitDynamicIndexerAccess(IDynamicIndexerAccessOperation operation, int? captureIdForResult)
        {
            return new DynamicIndexerAccessExpression(Visit(operation.Operation), VisitArray(operation.Arguments), ((HasDynamicArgumentsExpression)operation).ArgumentNames, ((HasDynamicArgumentsExpression)operation).ArgumentRefKinds, semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitDefaultValue(IDefaultValueOperation operation, int? captureIdForResult)
        {
            return new DefaultValueExpression(semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitTypeParameterObjectCreation(ITypeParameterObjectCreationOperation operation, int? captureIdForResult)
        {
            return new TypeParameterObjectCreationExpression(Visit(operation.Initializer), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitInvalid(IInvalidOperation operation, int? captureIdForResult)
        {
            return new InvalidOperation(VisitArray(operation.Children.ToImmutableArray()), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitLocalFunction(ILocalFunctionOperation operation, int? captureIdForResult)
        {
            return new LocalFunctionStatement(operation.Symbol, Visit(operation.Body), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitInterpolatedString(IInterpolatedStringOperation operation, int? captureIdForResult)
        {
            return new InterpolatedStringExpression(VisitArray(operation.Parts), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitInterpolatedStringText(IInterpolatedStringTextOperation operation, int? captureIdForResult)
        {
            return new InterpolatedStringText(Visit(operation.Text), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitInterpolation(IInterpolationOperation operation, int? captureIdForResult)
        {
            return new Interpolation(Visit(operation.Expression), Visit(operation.Alignment), Visit(operation.FormatString), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitIsPattern(IIsPatternOperation operation, int? captureIdForResult)
        {
            return new IsPatternExpression(Visit(operation.Value), Visit(operation.Pattern), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitConstantPattern(IConstantPatternOperation operation, int? captureIdForResult)
        {
            return new ConstantPattern(Visit(operation.Value), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitDeclarationPattern(IDeclarationPatternOperation operation, int? captureIdForResult)
        {
            return new DeclarationPattern(operation.DeclaredSymbol, semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitPatternCaseClause(IPatternCaseClauseOperation operation, int? captureIdForResult)
        {
            return new PatternCaseClause(operation.Label, Visit(operation.Pattern), Visit(operation.Guard), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitTuple(ITupleOperation operation, int? captureIdForResult)
        {
            return new TupleExpression(VisitArray(operation.Elements), semanticModel: null, operation.Syntax, operation.Type, operation.NaturalType, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitTranslatedQuery(ITranslatedQueryOperation operation, int? captureIdForResult)
        {
            return new TranslatedQueryExpression(Visit(operation.Operation), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitRaiseEvent(IRaiseEventOperation operation, int? captureIdForResult)
        {
            return new RaiseEventStatement(Visit(operation.EventReference), VisitArray(operation.Arguments), semanticModel: null, operation.Syntax, operation.Type, operation.ConstantValue, operation.IsImplicit);
        }

        public override IOperation VisitFlowCapture(IFlowCaptureOperation operation, int? captureIdForResult)
        {
            throw ExceptionUtilities.Unreachable;
        }

        public override IOperation VisitFlowCaptureReference(IFlowCaptureReferenceOperation operation, int? captureIdForResult)
        {
            throw ExceptionUtilities.Unreachable;
        }

        public override IOperation VisitIsNull(IIsNullOperation operation, int? captureIdForResult)
        {
            throw ExceptionUtilities.Unreachable;
        }
        #endregion
    }
}