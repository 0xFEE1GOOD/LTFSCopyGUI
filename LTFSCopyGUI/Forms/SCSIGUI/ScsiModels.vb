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
    <Category("节点"), Description("节点GUID")>
    Public Property Id As String = Guid.NewGuid().ToString()
    <Category("外观"), Description("名称")>
    Public Property Name As String = "NewElement"
    ' Field / Group / Page / PageList / Category
    <Category("节点"), Description("节点类型：Field / Group / Page / PageList / Category")>
    Public Property Kind As String = "Field"
    <Category("数据"), Description("作用范围：CDB / Param")>
    Public Property Area As ScsiArea = ScsiArea.Param

    <Category("数据"), Description("数据类型")>
    Public Property FieldType As ScsiFieldType = ScsiFieldType.IntegerValue
    <Category("数据"), Description("数据起始偏移bit数" & vbCrLf & "CDB为绝对偏移量; Param为相对于父节点偏移量")>
    Public Property BitOffset As Integer = 0
    <Category("数据"), Description("可选动态偏移公式，结果单位为 bit；可用 <元素GUID>.value 或 <元素GUID>.length 引用其他字段数值")>
    Public Property OffsetFormula As String = ""
    <Category("数据"), Description("可选存在条件公式；结果为 0 时该元素不占长度、不编码/解码，非 0 时启用。")>
    Public Property PresenceFormula As String = ""
    <Category("数据"), Description("数据长度 (bit)")>
    Public Property BitLength As Integer = 8
    <Category("数据"), Description("数据长度 (字节)" & vbCrLf & "用于ASCII或者Binary类型，或作为定长 Group / Page 时可选")>
    Public Property ByteLength As Integer = 0
    <Category("数据"), Description("数据长度表达式（字节）；优先于固定 ByteLength。支持 <GUID>.value、<GUID>.length 及 + - * / ()，引用的字段必须已解析。示例：<GUID>.value-3")>
    Public Property ByteLengthExpression As String = ""
    <Category("数据"), Description("是否有符号")>
    Public Property IsSigned As Boolean = False
    <Category("数据"), Description("默认值")>
    Public Property DefaultValue As String = "0"
    <Category("数据"), Description("枚举元素" & vbCrLf & "数据为枚举类型时的可选值列表。示例：" & vbCrLf & "Disabled=0;Enabled=1")>
    Public Property EnumItems As String = ""
    ' Safe arithmetic formula, e.g. <GUID>.length+4
    <Category("数据"), Description("数据填充公式，用于自动计算的值。示例：" & vbCrLf & "<GUID>.length+4")>
    Public Property Formula As String = ""
    <Category("数据"), Description("数据为自动计算的只读值")>
    Public Property ReadOnlyCalculated As Boolean = False
    ' PageList can allow multiple template IDs, separated by semicolon or comma.
    <Category("数据"), Description("节点类型为PageList时指定的模板ID。若AllowedPageTemplateIds非空则不生效")>
    Public Property PageTemplateId As String = ""
    <Category("数据"), Description("节点类型为PageList时允许的模板ID。用;或,分隔。若PageTemplateId和AllowedPageTemplateIds都为空则允许全部模板。")>
    Public Property AllowedPageTemplateIds As String = ""
    <Category("数据"), Description("节点类型为PageList时最小元素数量")>
    Public Property MinItems As Integer = 0
    <Category("数据"), Description("节点类型为PageList时最大元素数量")>
    Public Property MaxItems As Integer = 0
    ' Page template decode hints:
    <Category("解析"), Description("指定用于匹配判断的字段 ID")>
    Public Property MatchFieldId As String = ""
    <Category("解析"), Description("指定匹配字段应当满足的值")>
    Public Property MatchValue As String = ""
    <Category("解析"), Description("指定用于决定当前元素实际长度的字段 ID")>
    Public Property LengthFieldId As String = ""
    <Category("解析"), Description("指示长度字段的值是否包含当前结构的头部长度")>
    Public Property LengthIncludesHeader As Boolean = True
    <Category("节点"), Description("子元素")>
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
