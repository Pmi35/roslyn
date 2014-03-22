' Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports Microsoft.CodeAnalysis.Text
Imports Microsoft.CodeAnalysis.VisualBasic.Symbols
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax
Imports Roslyn.Test.Utilities

Namespace Microsoft.CodeAnalysis.VisualBasic.UnitTests.Semantics
    Public Class SelectCaseTest
        Inherits BasicTestBase
        <Fact()>
        Public Sub SelectCaseExpression_NothingLiteral()
            Dim compilation = CreateCompilationWithMscorlib(
        <compilation>
            <file name="a.vb"><![CDATA[
Public Module M
    Sub SelectCaseExpression()
        Select Case Nothing'BIND:"Nothing"
        End Select
    End Sub
End Module
    ]]></file>
        </compilation>)

            Dim semanticSummary = CompilationUtils.GetSemanticInfoSummary(Of LiteralExpressionSyntax)(compilation, "a.vb")

            Assert.Null(semanticSummary.Type)
            Assert.Equal("System.Object", semanticSummary.ConvertedType.ToTestDisplayString())
            Assert.Equal(TypeKind.Class, semanticSummary.ConvertedType.TypeKind)
            Assert.Equal(ConversionKind.WideningNothingLiteral, semanticSummary.ImplicitConversion.Kind)

            Assert.Null(semanticSummary.Symbol)
            Assert.Equal(CandidateReason.None, semanticSummary.CandidateReason)
            Assert.Equal(0, semanticSummary.CandidateSymbols.Length)

            Assert.Null(semanticSummary.Alias)

            Assert.Equal(0, semanticSummary.MemberGroup.Length)

            Assert.True(semanticSummary.ConstantValue.HasValue)
            Assert.Null(semanticSummary.ConstantValue.Value)
        End Sub

        <Fact()>
        Public Sub SelectCaseExpression_Literal()
            Dim compilation = CreateCompilationWithMscorlib(
        <compilation>
            <file name="a.vb"><![CDATA[
Public Module M
    Sub SelectCaseExpression()
        Select Case 1.1'BIND:"1.1"
        End Select
    End Sub
End Module
    ]]></file>
        </compilation>)

            Dim semanticSummary = CompilationUtils.GetSemanticInfoSummary(Of LiteralExpressionSyntax)(compilation, "a.vb")

            Assert.Equal("System.Double", semanticSummary.Type.ToTestDisplayString())
            Assert.Equal(TypeKind.Structure, semanticSummary.Type.TypeKind)
            Assert.Equal("System.Double", semanticSummary.ConvertedType.ToTestDisplayString())
            Assert.Equal(TypeKind.Structure, semanticSummary.ConvertedType.TypeKind)
            Assert.Equal(ConversionKind.Identity, semanticSummary.ImplicitConversion.Kind)

            Assert.Null(semanticSummary.Symbol)
            Assert.Equal(CandidateReason.None, semanticSummary.CandidateReason)
            Assert.Equal(0, semanticSummary.CandidateSymbols.Length)

            Assert.Null(semanticSummary.Alias)

            Assert.Equal(0, semanticSummary.MemberGroup.Length)

            Assert.True(semanticSummary.ConstantValue.HasValue)
            Assert.Equal(1.1, semanticSummary.ConstantValue.Value)
        End Sub

        <Fact()>
        Public Sub SelectCaseExpression_Local_IdentifierNameSyntax()
            Dim compilation = CreateCompilationWithMscorlib(
        <compilation>
            <file name="a.vb"><![CDATA[
Imports System
Module M1
    Sub SelectCaseExpression()
        Dim number As Integer = 0
        Select Case number'BIND:"number"
        End Select
    End Sub
Ehd Module
    ]]></file>
        </compilation>)

            Dim semanticSummary = CompilationUtils.GetSemanticInfoSummary(Of IdentifierNameSyntax)(compilation, "a.vb")

            Assert.Equal("System.Int32", semanticSummary.Type.ToTestDisplayString())
            Assert.Equal(TypeKind.Structure, semanticSummary.Type.TypeKind)
            Assert.Equal("System.Int32", semanticSummary.ConvertedType.ToTestDisplayString())
            Assert.Equal(TypeKind.Structure, semanticSummary.ConvertedType.TypeKind)
            Assert.Equal(ConversionKind.Identity, semanticSummary.ImplicitConversion.Kind)

            Assert.Equal("number As System.Int32", semanticSummary.Symbol.ToTestDisplayString())
            Assert.Equal(SymbolKind.Local, semanticSummary.Symbol.Kind)
            Assert.Equal(0, semanticSummary.CandidateSymbols.Length)

            Assert.Null(semanticSummary.Alias)

            Assert.Equal(0, semanticSummary.MemberGroup.Length)

            Assert.False(semanticSummary.ConstantValue.HasValue)
        End Sub

        <Fact()>
        Public Sub SelectCaseExpression_MethodCall_InvocationExpressionSyntax()
            Dim compilation = CreateCompilationWithMscorlib(
        <compilation>
            <file name="a.vb"><![CDATA[
Imports System
Module M1
    Function Foo() As Integer
        Console.WriteLine("Foo")
        Return 0
    End Function

    Sub SelectCaseExpression()
        Select Case Foo()'BIND:"Foo()"
        End Select
    End Sub
End Module
    ]]></file>
        </compilation>)

            Dim semanticSummary = CompilationUtils.GetSemanticInfoSummary(Of InvocationExpressionSyntax)(compilation, "a.vb")

            Assert.Equal("System.Int32", semanticSummary.Type.ToTestDisplayString())
            Assert.Equal(TypeKind.Structure, semanticSummary.Type.TypeKind)
            Assert.Equal("System.Int32", semanticSummary.ConvertedType.ToTestDisplayString())
            Assert.Equal(TypeKind.Structure, semanticSummary.ConvertedType.TypeKind)
            Assert.Equal(ConversionKind.Identity, semanticSummary.ImplicitConversion.Kind)

            Assert.Equal("Function M1.Foo() As System.Int32", semanticSummary.Symbol.ToTestDisplayString())
            Assert.Equal(SymbolKind.Method, semanticSummary.Symbol.Kind)
            Assert.Equal(0, semanticSummary.CandidateSymbols.Length)

            Assert.Null(semanticSummary.Alias)

            Assert.Equal(0, semanticSummary.MemberGroup.Length)

            Assert.False(semanticSummary.ConstantValue.HasValue)
        End Sub

        <Fact()>
        Public Sub SelectCaseExpression_MethodCall_IdentifierNameSyntax()
            Dim compilation = CreateCompilationWithMscorlib(
        <compilation>
            <file name="a.vb"><![CDATA[
Imports System
Module M1
    Function Foo() As Integer
        Console.WriteLine("Foo")
        Return 0
    End Function

    Sub SelectCaseExpression()
        Select Case Foo()'BIND:"Foo"
        End Select
    End Sub
End Module
    ]]></file>
        </compilation>)

            Dim semanticSummary = CompilationUtils.GetSemanticInfoSummary(Of IdentifierNameSyntax)(compilation, "a.vb")

            Assert.Null(semanticSummary.Type)
            Assert.Null(semanticSummary.ConvertedType)
            Assert.Equal(ConversionKind.Identity, semanticSummary.ImplicitConversion.Kind)

            Assert.Equal("Function M1.Foo() As System.Int32", semanticSummary.Symbol.ToTestDisplayString())
            Assert.Equal(SymbolKind.Method, semanticSummary.Symbol.Kind)
            Assert.Equal(0, semanticSummary.CandidateSymbols.Length)

            Assert.Null(semanticSummary.Alias)

            Assert.Equal(1, semanticSummary.MemberGroup.Length)
            Dim sortedMethodGroup = semanticSummary.MemberGroup.AsEnumerable().OrderBy(Function(s) s.ToTestDisplayString()).ToArray()
            Assert.Equal("Function M1.Foo() As System.Int32", sortedMethodGroup(0).ToTestDisplayString())

            Assert.False(semanticSummary.ConstantValue.HasValue)
        End Sub

        <Fact()>
        Public Sub SelectCaseExpression_Lambda()
            Dim compilation = CreateCompilationWithMscorlib(
        <compilation>
            <file name="a.vb"><![CDATA[
Public Module M
    Sub SelectCaseExpression()
        Select Case (Function(arg) arg Is Nothing)'BIND:"Function(arg) arg Is Nothing"
        End Select
    End Sub
End Module
    ]]></file>
        </compilation>)

            Dim semanticSummary = CompilationUtils.GetSemanticInfoSummary(Of SingleLineLambdaExpressionSyntax)(compilation, "a.vb")

            Assert.Null(semanticSummary.Type)
            Assert.Equal("Function <generated method>(arg As System.Object) As System.Boolean", semanticSummary.ConvertedType.ToTestDisplayString())
            Assert.Equal(TypeKind.Delegate, semanticSummary.ConvertedType.TypeKind)
            Assert.Equal(ConversionKind.Widening Or ConversionKind.Lambda, semanticSummary.ImplicitConversion.Kind)

            Assert.Equal("Function (arg As System.Object) As System.Boolean", semanticSummary.Symbol.ToTestDisplayString())
            Assert.Equal(SymbolKind.Method, semanticSummary.Symbol.Kind)
            Assert.Equal(0, semanticSummary.CandidateSymbols.Length)

            Assert.Null(semanticSummary.Alias)

            Assert.Equal(0, semanticSummary.MemberGroup.Length)

            Assert.False(semanticSummary.ConstantValue.HasValue)
        End Sub

        <Fact()>
        Public Sub SelectCaseExpression_ParenthesizedLambda()
            Dim compilation = CreateCompilationWithMscorlib(
        <compilation>
            <file name="a.vb"><![CDATA[
Public Module M
    Sub SelectCaseExpression()
        Select Case (Function(arg) arg Is Nothing)'BIND:"(Function(arg) arg Is Nothing)"
        End Select
    End Sub
End Module
    ]]></file>
        </compilation>)

            Dim semanticSummary = CompilationUtils.GetSemanticInfoSummary(Of ParenthesizedExpressionSyntax)(compilation, "a.vb")

            Assert.Equal("Function <generated method>(arg As System.Object) As System.Boolean", semanticSummary.Type.ToTestDisplayString())
            Assert.Equal(TypeKind.Delegate, semanticSummary.Type.TypeKind)
            Assert.Equal("Function <generated method>(arg As System.Object) As System.Boolean", semanticSummary.ConvertedType.ToTestDisplayString())
            Assert.Equal(TypeKind.Delegate, semanticSummary.ConvertedType.TypeKind)
            Assert.Equal(ConversionKind.Identity, semanticSummary.ImplicitConversion.Kind)

            Assert.Null(semanticSummary.Symbol)
            Assert.Equal(CandidateReason.None, semanticSummary.CandidateReason)
            Assert.Equal(0, semanticSummary.CandidateSymbols.Length)

            Assert.Null(semanticSummary.Alias)

            Assert.Equal(0, semanticSummary.MemberGroup.Length)

            Assert.False(semanticSummary.ConstantValue.HasValue)
        End Sub

        <Fact()>
        Public Sub SelectCaseExpression_Error_NotAValue_InvocationExpressionSyntax()
            Dim compilation = CreateCompilationWithMscorlib(
        <compilation>
            <file name="a.vb"><![CDATA[
Imports System
Module M1
    Sub Foo()
    End Sub

    Sub SelectCaseExpression(number As Integer)
        Select Case Foo()'BIND:"Foo()"
        End Select
    End Sub
End Module
    ]]></file>
        </compilation>)

            Dim semanticSummary = CompilationUtils.GetSemanticInfoSummary(Of InvocationExpressionSyntax)(compilation, "a.vb")

            Assert.Equal("System.Void", semanticSummary.Type.ToTestDisplayString())
            Assert.Equal(TypeKind.Structure, semanticSummary.Type.TypeKind)
            Assert.Equal("System.Void", semanticSummary.ConvertedType.ToTestDisplayString())
            Assert.Equal(TypeKind.Structure, semanticSummary.ConvertedType.TypeKind)
            Assert.Equal(ConversionKind.Identity, semanticSummary.ImplicitConversion.Kind)

            Assert.Null(semanticSummary.Symbol)
            Assert.Equal(CandidateReason.NotAValue, semanticSummary.CandidateReason)
            Assert.Equal(1, semanticSummary.CandidateSymbols.Length)
            Dim sortedCandidates = semanticSummary.CandidateSymbols.AsEnumerable().OrderBy(Function(s) s.ToTestDisplayString()).ToArray()
            Assert.Equal("Sub M1.Foo()", sortedCandidates(0).ToTestDisplayString())
            Assert.Equal(SymbolKind.Method, sortedCandidates(0).Kind)

            Assert.Null(semanticSummary.Alias)

            Assert.Equal(0, semanticSummary.MemberGroup.Length)

            Assert.False(semanticSummary.ConstantValue.HasValue)
        End Sub

        <Fact()>
        Public Sub SelectCaseExpression_Error_NotAValue_IdentifierNameSyntax()
            Dim compilation = CreateCompilationWithMscorlib(
        <compilation>
            <file name="a.vb"><![CDATA[
Imports System
Module M1
    Sub Foo()
    End Sub

    Sub SelectCaseExpression(number As Integer)
        Select Case Foo'BIND:"Foo"
        End Select
    End Sub
End Module
    ]]></file>
        </compilation>)

            Dim semanticSummary = CompilationUtils.GetSemanticInfoSummary(Of IdentifierNameSyntax)(compilation, "a.vb")

            Assert.Equal("System.Void", semanticSummary.Type.ToTestDisplayString())
            Assert.Equal(TypeKind.Structure, semanticSummary.Type.TypeKind)
            Assert.Equal("System.Void", semanticSummary.ConvertedType.ToTestDisplayString())
            Assert.Equal(TypeKind.Structure, semanticSummary.ConvertedType.TypeKind)
            Assert.Equal(ConversionKind.Identity, semanticSummary.ImplicitConversion.Kind)

            Assert.Null(semanticSummary.Symbol)
            Assert.Equal(CandidateReason.NotAValue, semanticSummary.CandidateReason)
            Assert.Equal(1, semanticSummary.CandidateSymbols.Length)
            Dim sortedCandidates = semanticSummary.CandidateSymbols.AsEnumerable().OrderBy(Function(s) s.ToTestDisplayString()).ToArray()
            Assert.Equal("Sub M1.Foo()", sortedCandidates(0).ToTestDisplayString())
            Assert.Equal(SymbolKind.Method, sortedCandidates(0).Kind)

            Assert.Null(semanticSummary.Alias)

            Assert.Equal(0, semanticSummary.MemberGroup.Length)

            Assert.False(semanticSummary.ConstantValue.HasValue)
        End Sub

        <Fact()>
        Public Sub SelectCaseExpression_Error_OverloadResolutionFailure()
            Dim compilation = CreateCompilationWithMscorlib(
        <compilation>
            <file name="a.vb"><![CDATA[
Imports System
Module M1
    Sub Foo(i As Integer)
    End Sub

    Sub SelectCaseExpression(number As Integer)
        Select Case Foo'BIND:"Foo"
        End Select
    End Sub
End Module
    ]]></file>
        </compilation>)

            Dim semanticSummary = CompilationUtils.GetSemanticInfoSummary(Of IdentifierNameSyntax)(compilation, "a.vb")

            Assert.Null(semanticSummary.Type)
            Assert.Equal("System.Void", semanticSummary.ConvertedType.ToTestDisplayString())
            Assert.Equal(TypeKind.Structure, semanticSummary.ConvertedType.TypeKind)
            Assert.Equal(ConversionKind.Identity, semanticSummary.ImplicitConversion.Kind)

            Assert.Null(semanticSummary.Symbol)
            Assert.Equal(CandidateReason.OverloadResolutionFailure, semanticSummary.CandidateReason)
            Assert.Equal(1, semanticSummary.CandidateSymbols.Length)
            Dim sortedCandidates = semanticSummary.CandidateSymbols.AsEnumerable().OrderBy(Function(s) s.ToTestDisplayString()).ToArray()
            Assert.Equal("Sub M1.Foo(i As System.Int32)", sortedCandidates(0).ToTestDisplayString())
            Assert.Equal(SymbolKind.Method, sortedCandidates(0).Kind)

            Assert.Null(semanticSummary.Alias)

            Assert.Equal(1, semanticSummary.MemberGroup.Length)
            Dim sortedMethodGroup = semanticSummary.MemberGroup.AsEnumerable().OrderBy(Function(s) s.ToTestDisplayString()).ToArray()
            Assert.Equal("Sub M1.Foo(i As System.Int32)", sortedMethodGroup(0).ToTestDisplayString())

            Assert.False(semanticSummary.ConstantValue.HasValue)
        End Sub

        <Fact()>
        Public Sub SelectCase_CaseRelationalClauseExpression_Literal()
            Dim compilation = CreateCompilationWithMscorlib(
        <compilation>
            <file name="a.vb"><![CDATA[
Imports System
Module M1
    Sub CaseRelationalClauseExpression(number As Integer)
        Select Case number
            Case Is < 1'BIND:"1"
        End Select
    End Sub
End Module
    ]]></file>
        </compilation>)

            Dim semanticSummary = CompilationUtils.GetSemanticInfoSummary(Of LiteralExpressionSyntax)(compilation, "a.vb")

            Assert.Equal("System.Int32", semanticSummary.Type.ToTestDisplayString())
            Assert.Equal(TypeKind.Structure, semanticSummary.Type.TypeKind)
            Assert.Equal("System.Int32", semanticSummary.ConvertedType.ToTestDisplayString())
            Assert.Equal(TypeKind.Structure, semanticSummary.ConvertedType.TypeKind)
            Assert.Equal(ConversionKind.Identity, semanticSummary.ImplicitConversion.Kind)

            Assert.Null(semanticSummary.Symbol)
            Assert.Equal(CandidateReason.None, semanticSummary.CandidateReason)
            Assert.Equal(0, semanticSummary.CandidateSymbols.Length)

            Assert.Null(semanticSummary.Alias)

            Assert.Equal(0, semanticSummary.MemberGroup.Length)

            Assert.True(semanticSummary.ConstantValue.HasValue)
            Assert.Equal(1, semanticSummary.ConstantValue.Value)
        End Sub

        <Fact()>
        Public Sub SelectCase_CaseRangeClauseExpression_MethodCall()
            Dim compilation = CreateCompilationWithMscorlib(
        <compilation>
            <file name="a.vb"><![CDATA[
Imports System
Module M1
    Function Foo() As Integer
        Return 0
    End Function

    Sub CaseRangeClauseExpression(number As Integer)
        Select Case number
            Case Foo() To 1'BIND:"Foo()"
        End Select
    End Sub
End Module
    ]]></file>
        </compilation>)

            Dim semanticSummary = CompilationUtils.GetSemanticInfoSummary(Of InvocationExpressionSyntax)(compilation, "a.vb")

            Assert.Equal("System.Int32", semanticSummary.Type.ToTestDisplayString())
            Assert.Equal(TypeKind.Structure, semanticSummary.Type.TypeKind)
            Assert.Equal("System.Int32", semanticSummary.ConvertedType.ToTestDisplayString())
            Assert.Equal(TypeKind.Structure, semanticSummary.ConvertedType.TypeKind)
            Assert.Equal(ConversionKind.Identity, semanticSummary.ImplicitConversion.Kind)

            Assert.Equal("Function M1.Foo() As System.Int32", semanticSummary.Symbol.ToTestDisplayString())
            Assert.Equal(SymbolKind.Method, semanticSummary.Symbol.Kind)
            Assert.Equal(0, semanticSummary.CandidateSymbols.Length)

            Assert.Null(semanticSummary.Alias)

            Assert.Equal(0, semanticSummary.MemberGroup.Length)

            Assert.False(semanticSummary.ConstantValue.HasValue)
        End Sub

        <Fact()>
        Public Sub SelectCase_CaseValueClauseExpression_DateTime()
            Dim compilation = CreateCompilationWithMscorlib(
        <compilation>
            <file name="a.vb"><![CDATA[
Imports System
Module M1
    Function Foo() As Integer
        Return 0
    End Function

    Sub CaseValueClauseExpression(number As Integer)
        Select Case number
            Case #8/13/2002 12:14 PM#'BIND:"#8/13/2002 12:14 PM#"
        End Select
    End Sub
End Module
    ]]></file>
        </compilation>)

            Dim semanticSummary = CompilationUtils.GetSemanticInfoSummary(Of LiteralExpressionSyntax)(compilation, "a.vb")

            Assert.Equal("System.DateTime", semanticSummary.Type.ToTestDisplayString())
            Assert.Equal(TypeKind.Structure, semanticSummary.Type.TypeKind)
            Assert.Equal("System.Int32", semanticSummary.ConvertedType.ToTestDisplayString())
            Assert.Equal(TypeKind.Structure, semanticSummary.ConvertedType.TypeKind)
            Assert.Equal(ConversionKind.DelegateRelaxationLevelNone, semanticSummary.ImplicitConversion.Kind)

            Assert.Null(semanticSummary.Symbol)
            Assert.Equal(CandidateReason.None, semanticSummary.CandidateReason)
            Assert.Equal(0, semanticSummary.CandidateSymbols.Length)

            Assert.Null(semanticSummary.Alias)

            Assert.Equal(0, semanticSummary.MemberGroup.Length)

            Assert.True(semanticSummary.ConstantValue.HasValue)
            Assert.Equal(#8/13/2002 12:14:00 PM#, semanticSummary.ConstantValue.Value)
        End Sub

        <WorkItem(543098)>
        <Fact()>
        Public Sub SelectCase_BoundLocal()
            Dim compilation = CreateCompilationWithMscorlib(
<compilation>
    <file name="a.vb"><![CDATA[
Imports System

Class Program
    Sub Test()
        Dim i As Integer = 10
        Select Case i'BIND:"i"
            Case NewMethod()
                Console.Write(5)
        End Select
    End Sub

    Private Shared Function NewMethod() As Integer
        Return 5
    End Function
End Class
    ]]></file>
</compilation>)

            Dim semanticSummary = CompilationUtils.GetSemanticInfoSummary(Of IdentifierNameSyntax)(compilation, "a.vb")

            Assert.Equal("System.Int32", semanticSummary.Type.ToTestDisplayString())
            Assert.Equal(TypeKind.Structure, semanticSummary.Type.TypeKind)
            Assert.Equal("System.Int32", semanticSummary.ConvertedType.ToTestDisplayString())
            Assert.Equal(TypeKind.Structure, semanticSummary.ConvertedType.TypeKind)
            Assert.Equal(ConversionKind.Identity, semanticSummary.ImplicitConversion.Kind)

            Assert.Equal("i As System.Int32", semanticSummary.Symbol.ToTestDisplayString())
            Assert.Equal(SymbolKind.Local, semanticSummary.Symbol.Kind)
            Assert.Equal(0, semanticSummary.CandidateSymbols.Length)

            Assert.Null(semanticSummary.Alias)

            Assert.Equal(0, semanticSummary.MemberGroup.Length)

            Assert.False(semanticSummary.ConstantValue.HasValue)
        End Sub

        <WorkItem(543387)>
        <Fact()>
        Public Sub SelectCase_AnonymousLambda()
            Dim compilation = CreateCompilationWithMscorlibAndVBRuntime(
<compilation>
    <file name="a.vb"><![CDATA[
Module Program
    Sub Main()
        Select Case Nothing
            Case Function() 5
                System.Console.WriteLine("Failed")
            Case Else
                System.Console.WriteLine("Succeeded")
        End Select
    End Sub
End Module
    ]]></file>
</compilation>, options:=OptionsExe.WithOptionStrict(OptionStrict.Custom))

            CompileAndVerify(compilation, expectedOutput:=
            <![CDATA[
Succeeded
]]>)

            AssertTheseDiagnostics(compilation,
<expected>
BC42036: Operands of type Object used in expressions for 'Select', 'Case' statements; runtime errors could occur.
        Select Case Nothing
                    ~~~~~~~
BC42016: Implicit conversion from 'Object' to 'Boolean'.
            Case Function() 5
                 ~~~~~~~~~~~~
</expected>)
        End Sub

        <Fact>
        Public Sub SelectCase_IdentityCaseClause()

            Dim verifier = CompileAndVerify(
<compilation>
    <file name="Program.vb">
Imports System
Imports System.Console

Module Program
    Sub Main()
        Dim obj As Object = "Hello, World!"

        Select Case obj
            Case Is Nothing
                Write("Null")
            Case IsNot Nothing
                Write("Not Null")
        End Select
    End Sub
End Module
    </file>
</compilation>,
expectedOutput:="Not Null")

        End Sub

        <Fact>
        Public Sub SelectCase_CaseWhenClause()

            Dim verifier = CompileAndVerify(
<compilation>
    <file name="Program.vb">
Imports System
Imports System.Console

Module Program
    Sub Main()
        Dim obj As Object = "Hello, World!"

        Select Case True
            Case True When obj Is Nothing
                Write("Null")
            Case True When obj IsNot Nothing
                Write("Not Null")
        End Select
    End Sub
End Module
    </file>
</compilation>,
expectedOutput:="Not Null")

        End Sub

        <Fact>
        Public Sub SelectCase_TypeCaseClause()

            Dim verifier = CompileAndVerify(
<compilation>
    <file name="Program.vb">
Imports System
Imports System.Console

Module Program
    Sub Main()
        Dim obj As Object = "Hello, World!"

        Select Case obj
            Case arr As String()
                Write(arr.Length)
            Case s As String When s.Length = 5
                Write(s)
            Case s As String
                Write(s)
        End Select
    End Sub
End Module   
    </file>
</compilation>,
expectedOutput:="Hello, World!")

        End Sub

        <Fact>
        Public Sub SelectCase_TypeCaseClause_ValueType()

            Dim verifier = CompileAndVerify(
<compilation>
    <file name="Program.vb">
Imports System
Imports System.Console

Module Program
    Sub Main()
        Dim obj As Object = 1

        Select Case obj
            Case s As String When s.Length = 5
                Write(s)
            Case i As Integer?
                Write(i.HasValue)
                Write(i.Value)
            Case Else
                Write("?")
        End Select

        Select Case obj
            Case s As String When s.Length = 5
                Write(s)
            Case i As Integer When i > 0
                Write(i)
            Case i As Integer
                Write("Zero")
        End Select
    End Sub
End Module   
    </file>
</compilation>,
expectedOutput:="True11")

        End Sub

        <Fact>
        Public Sub SelectCase_TypeCaseClause_TypeParameter()

            Dim verifier = CompileAndVerify(
<compilation>
    <file name="Program.vb">
Imports System
Imports System.Console

Module Program
    Sub Main()
        M(1, 1)
        M(1, 2)
        M("1", 1)
        N("a", "a")
        N("a", "b")
        N("a", CStr(Nothing))
        N(Nothing, "b")
        N(1, "b")
    End Sub

    Sub M(Of T As {Structure, IEquatable(Of T)})(obj As Object, y As T)
        Select Case obj
            Case x As T When x.Equals(y) 
                Write("Equal")
            Case x As T? 
                Write("Not Equal")
            Case Else
                Write("?")
        End Select
    End Sub

    Sub N(Of T As {Class, IEquatable(Of T)})(obj As Object, y As T)
        Select Case obj
            Case x As T When x.Equals(y) 
                Write("Equal")
            Case x As T When y IsNot Nothing
                Write("Not Equal")
            Case x As T
                Write("y:Null")
            Case Is Nothing
                Write("obj:Null")           
            Case Else
                Write("?")
        End Select
    End Sub

End Module   
    </file>
</compilation>,
expectedOutput:="EqualNot Equal?EqualNot Equaly:Nullobj:Null?")

        End Sub

        <Fact>
        Public Sub SelectCase_TypeCaseClause_TypeParameterError()

            Dim compilation = CompilationUtils.CreateCompilationWithMscorlibAndVBRuntime(
<compilation>
    <file name="Program.vb">
Imports System.Console

Module Program
    Sub Main()
    End Sub

    Sub M(Of T)(obj As Object)
        Select Case obj
            Case x As T
                Write("!")
            Case Else
                Write("?")
        End Select
    End Sub
End Module   
    </file>
</compilation>)

            VerifyDiagnostics(compilation, Diagnostic(ERRID.ERR_TryCastOfUnconstrainedTypeParam1, "x As T").WithArguments("T").WithLocation(9, 18))

        End Sub

    End Class
End Namespace
