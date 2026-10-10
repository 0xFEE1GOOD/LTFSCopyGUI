Option Strict On
Option Explicit On

Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Xml.Linq

Public NotInheritable Class XmlTemplateStore
    Private Sub New()
    End Sub

    Public Shared Function TemplateDirectory() As String
        Return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "template")
    End Function

    Public Shared Function ListTemplates() As List(Of String)
        Dim dir = TemplateDirectory()
        If Not Directory.Exists(dir) Then Directory.CreateDirectory(dir)
        Return Directory.GetFiles(dir, "*.xml").Select(Function(p) Path.GetFileNameWithoutExtension(p)).OrderBy(Function(s) s).ToList()
    End Function

    Public Shared Function LoadTemplate(path As String) As ScsiTemplate
        Dim doc = XDocument.Load(path)
        Dim root = doc.Root
        If root Is Nothing OrElse root.Name <> "ScsiTemplate" Then Throw New InvalidDataException("根节点必须是 ScsiTemplate。")
        Dim model As New ScsiTemplate With {.DeviceType = Attr(root, "deviceType", IO.Path.GetFileNameWithoutExtension(path))}
        Dim pages = root.Element("PageTemplates")
        If pages IsNot Nothing Then
            For Each e In pages.Elements("Element")
                model.PageTemplates.Add(ParseElement(e))
            Next
        End If
        Dim commands = root.Element("Commands")
        If commands IsNot Nothing Then
            For Each c In commands.Elements("Command")
                Dim cmd As New ScsiCommandDef With {
                    .Id = Attr(c, "id", Guid.NewGuid().ToString()),
                    .Name = Attr(c, "name", "Unnamed"),
                    .Opcode = IntAttr(c, "opcode", 0),
                    .CdbLength = IntAttr(c, "cdbLength", 6),
                    .DataIn = CType(IntAttr(c, "dataIn", 0), ScsiDataIn),
                    .Description = Attr(c, "description", "")
                }
                Dim els = c.Element("Elements")
                If els IsNot Nothing Then
                    For Each e In els.Elements("Element")
                        cmd.Elements.Add(ParseElement(e))
                    Next
                End If
                model.Commands.Add(cmd)
            Next
        End If
        ValidateTemplate(model)
        Return model
    End Function

    Public Shared Sub SaveTemplate(path As String, model As ScsiTemplate)
        ValidateTemplate(model)
        Dim root As New XElement("ScsiTemplate", New XAttribute("deviceType", model.DeviceType))
        Dim pages As New XElement("PageTemplates")
        For Each p In model.PageTemplates
            pages.Add(WriteElement(p))
        Next
        Dim commands As New XElement("Commands")
        For Each c In model.Commands
            Dim ce As New XElement("Command",
                New XAttribute("id", c.Id), New XAttribute("name", c.Name),
                New XAttribute("opcode", c.Opcode), New XAttribute("cdbLength", c.CdbLength),
                New XAttribute("dataIn", CInt(c.DataIn)), New XAttribute("description", c.Description))
            Dim els As New XElement("Elements")
            For Each e In c.Elements
                els.Add(WriteElement(e))
            Next
            ce.Add(els)
            commands.Add(ce)
        Next
        root.Add(pages, commands)
        Dim dir = IO.Path.GetDirectoryName(path)
        If Not String.IsNullOrEmpty(dir) Then Directory.CreateDirectory(dir)
        Dim xd = New XDocument(New XDeclaration("1.0", "utf-8", "yes"), root)
        xd.Save(path)
    End Sub

    Private Shared Function ParseElement(e As XElement) As ScsiElementDef
        Dim m As New ScsiElementDef With {
            .Id = Attr(e, "id", Guid.NewGuid().ToString()),
            .Name = Attr(e, "name", "Element"),
            .Kind = Attr(e, "kind", "Field"),
            .Area = CType([Enum].Parse(GetType(ScsiArea), Attr(e, "area", "Param"), True), ScsiArea),
            .FieldType = CType([Enum].Parse(GetType(ScsiFieldType), Attr(e, "fieldType", "IntegerValue"), True), ScsiFieldType),
            .BitOffset = IntAttr(e, "bitOffset", 0),
            .OffsetFormula = Attr(e, "offsetFormula", ""),
            .PresenceFormula = Attr(e, "presenceFormula", ""),
            .BitLength = IntAttr(e, "bitLength", 8),
            .ByteLength = IntAttr(e, "byteLength", 0),
            .IsSigned = BoolAttr(e, "signed", False),
            .DefaultValue = Attr(e, "default", "0"),
            .EnumItems = Attr(e, "enumItems", ""),
            .Formula = Attr(e, "formula", ""),
            .ReadOnlyCalculated = BoolAttr(e, "calculated", False),
            .PageTemplateId = Attr(e, "pageTemplateId", ""),
            .AllowedPageTemplateIds = Attr(e, "allowedPageTemplateIds", ""),
            .MinItems = IntAttr(e, "minItems", 0),
            .MaxItems = IntAttr(e, "maxItems", 0),
            .MatchFieldId = Attr(e, "matchFieldId", ""),
            .MatchValue = Attr(e, "matchValue", ""),
            .LengthFieldId = Attr(e, "lengthFieldId", ""),
            .LengthIncludesHeader = BoolAttr(e, "lengthIncludesHeader", True)
        }
        For Each child In e.Elements("Element")
            m.Children.Add(ParseElement(child))
        Next
        Return m
    End Function

    Private Shared Function WriteElement(m As ScsiElementDef) As XElement
        Dim e As New XElement("Element",
            New XAttribute("id", m.Id), New XAttribute("name", m.Name),
            New XAttribute("kind", m.Kind), New XAttribute("area", m.Area.ToString()),
            New XAttribute("fieldType", m.FieldType.ToString()), New XAttribute("bitOffset", m.BitOffset),
            New XAttribute("offsetFormula", m.OffsetFormula), New XAttribute("presenceFormula", m.PresenceFormula), New XAttribute("bitLength", m.BitLength), New XAttribute("byteLength", m.ByteLength),
            New XAttribute("signed", m.IsSigned), New XAttribute("default", m.DefaultValue),
            New XAttribute("enumItems", m.EnumItems), New XAttribute("formula", m.Formula),
            New XAttribute("calculated", m.ReadOnlyCalculated), New XAttribute("pageTemplateId", m.PageTemplateId),
            New XAttribute("allowedPageTemplateIds", m.AllowedPageTemplateIds),
            New XAttribute("minItems", m.MinItems), New XAttribute("maxItems", m.MaxItems),
            New XAttribute("matchFieldId", m.MatchFieldId), New XAttribute("matchValue", m.MatchValue),
            New XAttribute("lengthFieldId", m.LengthFieldId), New XAttribute("lengthIncludesHeader", m.LengthIncludesHeader))
        For Each child In m.Children
            e.Add(WriteElement(child))
        Next
        Return e
    End Function

    Public Shared Sub ValidateTemplate(model As ScsiTemplate)
        Dim ids As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim all As New List(Of ScsiElementDef)
        For Each p In model.PageTemplates
            Collect(p, all)
        Next
        For Each c In model.Commands
            If c.CdbLength < 1 OrElse c.CdbLength > 260 Then Throw New InvalidDataException("CDB 长度无效：" & c.Name)
            For Each el In c.Elements
                Collect(el, all)
            Next
        Next
        For Each el In all
            If String.IsNullOrWhiteSpace(el.Id) Then Throw New InvalidDataException("元素 GUID 不能为空：" & el.Name)
            If Not ids.Add(el.Id) Then Throw New InvalidDataException("发现重复元素 GUID：" & el.Id)
            If el.BitOffset < 0 OrElse el.BitLength < 0 Then Throw New InvalidDataException("BitOffset/BitLength 不能为负数：" & el.Name)
            If el.Kind = "Field" AndAlso (el.FieldType = ScsiFieldType.IntegerValue OrElse el.FieldType = ScsiFieldType.EnumValue) AndAlso (el.BitLength < 1 OrElse el.BitLength > 64) Then
                Throw New InvalidDataException("整数/枚举字段位宽必须为 1–64：" & el.Name)
            End If
        Next
    End Sub

    Private Shared Sub Collect(e As ScsiElementDef, all As List(Of ScsiElementDef))
        all.Add(e)
        For Each c In e.Children
            Collect(c, all)
        Next
    End Sub

    Private Shared Function Attr(e As XElement, name As String, fallback As String) As String
        Dim a = e.Attribute(name)
        Return If(a Is Nothing, fallback, a.Value)
    End Function
    Private Shared Function IntAttr(e As XElement, name As String, fallback As Integer) As Integer
        Dim n As Integer
        Return If(Integer.TryParse(Attr(e, name, fallback.ToString()), n), n, fallback)
    End Function
    Private Shared Function BoolAttr(e As XElement, name As String, fallback As Boolean) As Boolean
        Dim b As Boolean
        Return If(Boolean.TryParse(Attr(e, name, fallback.ToString()), b), b, fallback)
    End Function
End Class
