Option Strict On
Option Explicit On

Imports System.ComponentModel

Public Enum ScsiDataIn
    _0_HostToDevice = 0
    _1_DeviceToHost = 1
    _2_Undefined = 2
End Enum

Public Enum ScsiFieldType
    IntegerValue
    Binary
    ASCII
    EnumValue
End Enum

Public Enum ScsiArea
    CDB
    Param
End Enum

<Serializable>
Public Class ScsiTemplate
    Public Property DeviceType As String = ""
    Public Property Commands As New List(Of ScsiCommandDef)()
    Public Property PageTemplates As New List(Of ScsiElementDef)()
End Class

<Serializable>
Public Class ScsiCommandDef
    Public Property Id As String = Guid.NewGuid().ToString()
    Public Property Name As String = ""
    Public Property Opcode As Integer
    Public Property CdbLength As Integer = 6
    Public Property DataIn As ScsiDataIn = ScsiDataIn._2_Undefined
    Public Property Description As String = ""
    Public Property Elements As New List(Of ScsiElementDef)()
End Class

<Serializable>
Public Class ScsiElementDef
    <Category("Node"), Description("Node GUID")>
    Public Property Id As String = Guid.NewGuid().ToString()
    <Category("Appearance"), Description("Name")>
    Public Property Name As String = "NewElement"
    ' Field / Group / Page / PageList / Category
    <Category("Node"), Description("Node type: Field / Group / Page / PageList / Category")>
    Public Property Kind As String = "Field"
    <Category("Data"), Description("Scope: CDB / Param")>
    Public Property Area As ScsiArea = ScsiArea.Param

    <Category("Data"), Description("Data type")>
    Public Property FieldType As ScsiFieldType = ScsiFieldType.IntegerValue
    <Category("Data"), Description("Start offset (bits)" & vbCrLf & "Absolute offset in a CDB; relative to the parent node in Param")>
    Public Property BitOffset As Integer = 0
    <Category("Data"), Description("Optional dynamic offset formula, in bits; use <element GUID>.value or <element GUID>.length to refer to other fields")>
    Public Property OffsetFormula As String = ""
    <Category("Data"), Description("Optional presence condition; when it evaluates to 0 the element takes no space and is not encoded or decoded, otherwise it is enabled.")>
    Public Property PresenceFormula As String = ""
    <Category("Data"), Description("Length (bits)")>
    Public Property BitLength As Integer = 8
    <Category("Data"), Description("Length (bytes)" & vbCrLf & "For ASCII or Binary types; optional for a fixed-length Group / Page")>
    Public Property ByteLength As Integer = 0
    <Category("Data"), Description("Length expression (bytes); takes precedence over a fixed ByteLength. Supports <GUID>.value, <GUID>.length and + - * / (); referenced fields must already be decoded. Example: <GUID>.value-3")>
    Public Property ByteLengthExpression As String = ""
    <Category("Data"), Description("Signed")>
    Public Property IsSigned As Boolean = False
    <Category("Data"), Description("Default value")>
    Public Property DefaultValue As String = "0"
    <Category("Data"), Description("Enum items" & vbCrLf & "Allowed values when the data is an enum. Example:" & vbCrLf & "Disabled=0;Enabled=1")>
    Public Property EnumItems As String = ""
    ' Safe arithmetic formula, e.g. <GUID>.length+4
    <Category("Data"), Description("Fill formula for automatically computed values. Example:" & vbCrLf & "<GUID>.length+4")>
    Public Property Formula As String = ""
    <Category("Data"), Description("The value is computed automatically (read-only)")>
    Public Property ReadOnlyCalculated As Boolean = False
    ' PageList can allow multiple template IDs, separated by semicolon or comma.
    <Category("Data"), Description("Template ID for a PageList node. Ignored when AllowedPageTemplateIds is set")>
    Public Property PageTemplateId As String = ""
    <Category("Data"), Description("Allowed template IDs for a PageList node, separated by ; or ,. If both PageTemplateId and AllowedPageTemplateIds are empty, all templates are allowed.")>
    Public Property AllowedPageTemplateIds As String = ""
    <Category("Data"), Description("Minimum item count for a PageList node")>
    Public Property MinItems As Integer = 0
    <Category("Data"), Description("Maximum item count for a PageList node")>
    Public Property MaxItems As Integer = 0
    ' Page template decode hints:
    <Category("Parsing"), Description("ID of the field used for matching")>
    Public Property MatchFieldId As String = ""
    <Category("Parsing"), Description("Value the match field must have")>
    Public Property MatchValue As String = ""
    <Category("Parsing"), Description("ID of the field that gives this element's actual length")>
    Public Property LengthFieldId As String = ""
    <Category("Parsing"), Description("Whether the length field's value includes this structure's header")>
    Public Property LengthIncludesHeader As Boolean = True
    <Category("Node"), Description("Child elements")>
    Public Property Children As New List(Of ScsiElementDef)()
End Class

' Runtime nodes are distinct from the reusable template definitions.
' A node path identifies each instance, including repeated instances of the same Page template.
Public Class ScsiValueNode
    Public Property Definition As ScsiElementDef
    Public Property Value As Object
    Public Property Children As New List(Of ScsiValueNode)()
    Public Property Items As New List(Of ScsiValueNode)()
    Public Property Parent As ScsiValueNode
    Public Property InstanceName As String = ""

    Public Overrides Function ToString() As String
        If Definition Is Nothing Then Return "(undefined)"
        Return If(String.IsNullOrEmpty(InstanceName), Definition.Name, InstanceName)
    End Function
End Class

Public Class CommandListItem
    Public ReadOnly Property Command As ScsiCommandDef
    Public Sub New(c As ScsiCommandDef)
        Command = c
    End Sub
    Public Overrides Function ToString() As String
        Return String.Format("{0}  (0x{1:X2})", Command.Name, Command.Opcode)
    End Function
End Class

Public Class EnumOption
    Public ReadOnly Property Label As String
    Public ReadOnly Property Value As String
    Public Sub New(label As String, value As String)
        Me.Label = label
        Me.Value = value
    End Sub
    Public Overrides Function ToString() As String
        ' 枚举选项的数值部分优先按十六进制显示；Value 仍保留原始值，避免影响编码逻辑。
        Dim raw As String = If(Value, String.Empty).Trim()
        Dim number As ULong
        If raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) Then
            Dim hexText As String = raw.Substring(2)
            If ULong.TryParse(hexText, Globalization.NumberStyles.HexNumber,
                              Globalization.CultureInfo.InvariantCulture, number) Then
                Return Label & " (0x" & number.ToString("X", Globalization.CultureInfo.InvariantCulture) & ")"
            End If
        ElseIf ULong.TryParse(raw, Globalization.NumberStyles.Integer,
                              Globalization.CultureInfo.InvariantCulture, number) Then
            Return Label & " (0x" & number.ToString("X", Globalization.CultureInfo.InvariantCulture) & ")"
        End If
        Return Label & " (" & Value & ")"
    End Function
End Class
