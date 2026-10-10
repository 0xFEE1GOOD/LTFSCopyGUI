Option Strict On
Option Explicit On

Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq
Imports System.Text
Imports System.Text.RegularExpressions

Public Class ScsiEncodedCommand
    Public Property CDB As Byte()
    Public Property Param As Byte()
End Class

Public NotInheritable Class ScsiCodec
    Private Sub New()
    End Sub

    Public Shared Function CreateRuntimeNodes(command As ScsiCommandDef, template As ScsiTemplate) As List(Of ScsiValueNode)
        Dim result As New List(Of ScsiValueNode)
        For Each def In command.Elements
            result.Add(CreateNode(def, template, Nothing))
        Next
        Return result
    End Function

    Public Shared Function CreateNode(def As ScsiElementDef, template As ScsiTemplate, parent As ScsiValueNode) As ScsiValueNode
        Dim n As New ScsiValueNode With {.Definition = def, .Parent = parent, .Value = def.DefaultValue}
        If def.Kind = "PageList" Then
            Dim allowed = GetAllowedPageTemplates(def, template)
            For i As Integer = 0 To Math.Max(0, def.MinItems) - 1
                If allowed.Count = 0 Then Exit For
                Dim item = CreateNode(allowed(0), template, n)
                item.InstanceName = allowed(0).Name & " [" & (i + 1).ToString() & "]"
                n.Items.Add(item)
            Next
        Else
            For Each childDef In def.Children
                n.Children.Add(CreateNode(childDef, template, n))
            Next
        End If
        Return n
    End Function

    ''' <summary>
    ''' 递归收集所有真正的 Page 模板。
    ''' Category 仅用于分组，不作为可解码的 Page 模板。
    ''' </summary>
    Public Shared Function GetAllPageTemplates(template As ScsiTemplate) As List(Of ScsiElementDef)
        Dim result As New List(Of ScsiElementDef)
        If template Is Nothing OrElse template.PageTemplates Is Nothing Then
            Return result
        End If
        For Each item As ScsiElementDef In template.PageTemplates
            CollectPageTemplates(item, result)
        Next
        Return result
    End Function

    Private Shared Sub CollectPageTemplates(element As ScsiElementDef, result As List(Of ScsiElementDef))
        If element Is Nothing Then Return
        If String.Equals(element.Kind, "Category", StringComparison.OrdinalIgnoreCase) Then
            ' 分类节点不加入结果，只递归收集其子节点。
            If element.Children IsNot Nothing Then
                For Each child As ScsiElementDef In element.Children
                    CollectPageTemplates(child, result)
                Next
            End If
        Else
            ' 非 Category 节点视为实际 Page 模板。
            result.Add(element)
        End If
    End Sub

    Public Shared Function GetAllowedPageTemplates(listDef As ScsiElementDef, template As ScsiTemplate) As List(Of ScsiElementDef)
        Dim allPages As List(Of ScsiElementDef) = GetAllPageTemplates(template)
        Dim ids = Regex.Split(If(String.IsNullOrWhiteSpace(listDef.AllowedPageTemplateIds), listDef.PageTemplateId, listDef.AllowedPageTemplateIds), "[;,]")
        Dim selected = ids.Select(Function(s) s.Trim()).Where(Function(s) s <> "").ToList()
        If selected.Count = 0 Then Return allPages
        Return allPages.Where(Function(p) selected.Any(Function(id) String.Equals(id, p.Id, StringComparison.OrdinalIgnoreCase) OrElse String.Equals(id, p.Name, StringComparison.OrdinalIgnoreCase))).ToList()
    End Function

    Public Shared Function Encode(command As ScsiCommandDef, template As ScsiTemplate, nodes As List(Of ScsiValueNode)) As ScsiEncodedCommand
        If command.CdbLength < 1 OrElse command.CdbLength > 260 Then Throw New InvalidOperationException("CDB 长度必须在 1–260 字节范围内。")
        _currentNodes = nodes
        ValidatePageLists(nodes, template)
        RecalculateFormulas(nodes)
        Dim cdb(command.CdbLength - 1) As Byte
        Dim paramLength = RequiredAreaBytes(nodes, ScsiArea.Param)
        Dim param As Byte()
        If paramLength > 0 Then
            ReDim param(paramLength - 1)
        Else
            param = New Byte() {}
        End If

        For Each node In nodes
            If node.Definition.Area = ScsiArea.CDB Then
                WriteNode(node, cdb, 0, True)
            Else
                WriteNode(node, param, 0, True)
            End If
        Next
        Return New ScsiEncodedCommand With {.CDB = cdb, .Param = param}
    End Function

    Public Shared Sub Decode(command As ScsiCommandDef, template As ScsiTemplate, nodes As List(Of ScsiValueNode), cdb As Byte(), param As Byte())
        If cdb Is Nothing OrElse cdb.Length < command.CdbLength Then Throw New ArgumentException("CDB 数据长度不足。", NameOf(cdb))
        _currentTemplate = template
        _currentNodes = nodes
        Try
            For Each node In nodes
                If node.Definition.Area = ScsiArea.CDB Then
                    DecodeNode(node, cdb, 0, True, cdb.Length)
                Else
                    DecodeNode(node, If(param, New Byte() {}), 0, True, If(param Is Nothing, 0, param.Length))
                End If
            Next
            RecalculateFormulas(nodes)
        Finally
            _currentTemplate = Nothing
        End Try
    End Sub


    Private Shared Sub ValidatePageLists(nodes As IEnumerable(Of ScsiValueNode), template As ScsiTemplate)
        For Each node In Flatten(nodes)
            If node.Definition Is Nothing OrElse Not node.Definition.Kind.Equals("PageList", StringComparison.OrdinalIgnoreCase) Then Continue For
            Dim d = node.Definition
            If d.MinItems > 0 AndAlso node.Items.Count < d.MinItems Then
                Throw New InvalidOperationException(String.Format("{0} 至少需要 {1} 个 Sub Page，当前为 {2} 个。", d.Name, d.MinItems, node.Items.Count))
            End If
            If d.MaxItems > 0 AndAlso node.Items.Count > d.MaxItems Then
                Throw New InvalidOperationException(String.Format("{0} 最多允许 {1} 个 Sub Page，当前为 {2} 个。", d.Name, d.MaxItems, node.Items.Count))
            End If
            Dim allowed = GetAllowedPageTemplates(d, template)
            For Each item In node.Items
                If item.Definition Is Nothing OrElse Not allowed.Any(Function(p) String.Equals(p.Id, item.Definition.Id, StringComparison.OrdinalIgnoreCase)) Then
                    Throw New InvalidOperationException("PageList " & d.Name & " 中存在不允许的 Sub Page 模板。")
                End If
            Next
        Next
    End Sub

    Public Shared Sub RecalculateFormulas(nodes As List(Of ScsiValueNode))
        Dim all = Flatten(nodes).ToList()
        For Each node In all
            Dim d = node.Definition
            If d Is Nothing OrElse String.IsNullOrWhiteSpace(d.Formula) Then Continue For
            Dim result = BitCodec.EvaluateLengthFormula(d.Formula,
                Function(id)
                    Dim target = all.FirstOrDefault(Function(x) String.Equals(x.Definition.Id, id, StringComparison.OrdinalIgnoreCase))
                    If target Is Nothing Then Throw New InvalidOperationException("长度公式引用的元素不存在：" & id)
                    Return CLng((ElementExtentBits(target) + 7) \ 8)
                End Function)
            node.Value = result
        Next
    End Sub

    Public Shared Function RequiredAreaBytes(nodes As List(Of ScsiValueNode), area As ScsiArea) As Integer
        Dim endBit As Long = 0
        For Each n In nodes
            If n.Definition.Area <> area Then Continue For
            endBit = Math.Max(endBit, EffectiveOffsetBits(n) + ElementExtentBits(n))
        Next
        If endBit > Integer.MaxValue * 8L Then Throw New OverflowException("Param 长度过大。")
        Return CInt((endBit + 7) \ 8)
    End Function

    Private Shared _currentNodes As List(Of ScsiValueNode)

    ' 返回元素相对父节点的有效偏移（固定 BitOffset + 动态 OffsetFormula）。
    Private Shared Function EffectiveOffsetBits(node As ScsiValueNode) As Long
        Dim d = node.Definition
        If d Is Nothing Then Return 0
        Dim result As Long = d.BitOffset
        If Not String.IsNullOrWhiteSpace(d.OffsetFormula) Then
            Dim allNodes = If(_currentNodes, New List(Of ScsiValueNode)())
            result += BitCodec.EvaluateOffsetFormula(d.OffsetFormula,
                Function(id)
                    Dim target = Flatten(allNodes).FirstOrDefault(Function(x) x.Definition IsNot Nothing AndAlso String.Equals(x.Definition.Id, id, StringComparison.OrdinalIgnoreCase))
                    If target Is Nothing Then
                        Throw New InvalidOperationException("动态偏移公式引用的元素不存在：" & id)
                    End If

                    Return ParseInteger(target.Value)
                End Function,
                Function(id)
                    Dim target = Flatten(allNodes).FirstOrDefault(Function(x) x.Definition IsNot Nothing AndAlso String.Equals(x.Definition.Id, id, StringComparison.OrdinalIgnoreCase))

                    If target Is Nothing Then
                        Throw New InvalidOperationException("动态偏移公式引用的元素不存在：" & id)
                    End If

                    Return ElementExtentBits(target)
                End Function)
        End If
        If result < 0 OrElse result > Integer.MaxValue Then Throw New InvalidOperationException("元素动态偏移无效：" & d.Name)
        Return result
    End Function

    Private Shared Function IsNodePresent(node As ScsiValueNode) As Boolean
        Dim d = node.Definition
        If d Is Nothing OrElse String.IsNullOrWhiteSpace(d.PresenceFormula) Then Return True
        Dim allNodes = If(_currentNodes, New List(Of ScsiValueNode)())
        Return BitCodec.EvaluateOffsetFormula(d.PresenceFormula,
            Function(id)
                Dim target = Flatten(allNodes).FirstOrDefault(Function(x) x.Definition IsNot Nothing AndAlso String.Equals(x.Definition.Id, id, StringComparison.OrdinalIgnoreCase))
                If target Is Nothing Then Throw New InvalidOperationException("存在条件公式引用的元素不存在：" & id)
                Return ParseInteger(target.Value)
            End Function,
            Function(id)
                Dim target = Flatten(allNodes).FirstOrDefault(Function(x) x.Definition IsNot Nothing AndAlso String.Equals(x.Definition.Id, id, StringComparison.OrdinalIgnoreCase))
                If target Is Nothing Then Throw New InvalidOperationException("存在条件公式引用的元素不存在：" & id)
                Return ElementExtentBits(target)
            End Function) <> 0
    End Function

    Public Shared Function ElementExtentBits(node As ScsiValueNode) As Long
        Dim d = node.Definition
        If d Is Nothing OrElse Not IsNodePresent(node) Then Return 0
        Select Case d.Kind.ToUpperInvariant()
            Case "FIELD"
                If d.FieldType = ScsiFieldType.ASCII OrElse d.FieldType = ScsiFieldType.Binary Then
                    Return CLng(Math.Max(0, If(d.ByteLength > 0, d.ByteLength, (d.BitLength + 7) \ 8))) * 8L
                End If
                Return Math.Max(0, d.BitLength)
            Case "PAGELIST"
                If d.ByteLength > 0 Then Return CLng(d.ByteLength) * 8L
                Dim total As Long = 0
                For Each item In node.Items
                    total += PageExtentBits(item)
                Next
                Return total
            Case "GROUP", "PAGE"
                If d.ByteLength > 0 Then Return CLng(d.ByteLength) * 8L
                Dim endBit As Long = 0
                For Each child In node.Children
                    endBit = Math.Max(endBit, EffectiveOffsetBits(child) + ElementExtentBits(child))
                Next
                Return endBit
            Case Else
                Return 0
        End Select
    End Function

    Private Shared Function PageExtentBits(pageNode As ScsiValueNode) As Long
        Return ElementExtentBits(pageNode)
    End Function

    Private Shared Sub WriteNode(node As ScsiValueNode, target As Byte(), baseBit As Integer, root As Boolean)
        Dim d = node.Definition
        If d Is Nothing OrElse Not IsNodePresent(node) Then Return
        Dim startBit As Long = If(root, 0L, CLng(baseBit)) + EffectiveOffsetBits(node)
        If startBit < 0 OrElse startBit > Integer.MaxValue Then Throw New InvalidOperationException("元素偏移无效：" & d.Name)
        Select Case d.Kind.ToUpperInvariant()
            Case "FIELD"
                WriteField(node, target, CInt(startBit))
            Case "GROUP", "PAGE"
                For Each child In node.Children
                    WriteNode(child, target, CInt(startBit), False)
                Next
            Case "PAGELIST"
                Dim cursor As Long = startBit
                For Each item In node.Items
                    If (cursor Mod 8) <> 0 Then Throw New InvalidOperationException("PageList 必须按字节对齐：" & d.Name)
                    WriteNode(item, target, CInt(cursor), False)
                    cursor += PageExtentBits(item)
                Next
        End Select
    End Sub

    Private Shared Sub WriteField(node As ScsiValueNode, target As Byte(), bitOffset As Integer)
        Dim d = node.Definition
        If d.Formula <> "" Then
            ' Value was recalculated immediately before encoding.
        End If
        If d.FieldType = ScsiFieldType.ASCII Then
            Dim count = If(d.ByteLength > 0, d.ByteLength, (d.BitLength + 7) \ 8)
            EnsureRange(target, bitOffset, count * 8, d.Name)
            Dim src = Encoding.ASCII.GetBytes(Convert.ToString(If(node.Value, ""), CultureInfo.InvariantCulture))
            For i As Integer = 0 To Math.Min(count, src.Length) - 1
                target(bitOffset \ 8 + i) = src(i)
            Next
            Return
        ElseIf d.FieldType = ScsiFieldType.Binary Then
            Dim count = If(d.ByteLength > 0, d.ByteLength, (d.BitLength + 7) \ 8)
            EnsureRange(target, bitOffset, count * 8, d.Name)
            Dim src = ParseHexBytes(Convert.ToString(If(node.Value, ""), CultureInfo.InvariantCulture))
            If src.Length > count Then Throw New InvalidOperationException(d.Name & " 的 Binary 数据超过指定长度 " & count & " 字节。")
            Array.Copy(src, 0, target, bitOffset \ 8, src.Length)
            Return
        End If

        Dim width = d.BitLength
        Dim u As ULong
        If d.IsSigned Then
            Dim signedValue As Long = ParseInteger(node.Value)
            If width < 64 Then
                Dim minValue As Long = -(1L << (width - 1))
                Dim maxValue As Long = (1L << (width - 1)) - 1L
                If signedValue < minValue OrElse signedValue > maxValue Then Throw New InvalidOperationException(d.Name & " 超出有符号 " & width & " 位范围。")
                u = BitConverter.ToUInt64(BitConverter.GetBytes(signedValue), 0) And ((1UL << width) - 1UL)
            Else
                u = BitConverter.ToUInt64(BitConverter.GetBytes(signedValue), 0)
            End If
        Else
            u = ParseUnsigned(node.Value)
            If width < 64 AndAlso u >= (1UL << width) Then Throw New InvalidOperationException(d.Name & " 超出 " & width & " 位范围。")
        End If
        BitCodec.WriteUnsigned(target, bitOffset, width, u)
    End Sub

    Private Shared Sub DecodeNode(node As ScsiValueNode, source As Byte(), baseBit As Integer, root As Boolean, limitBytes As Integer)
        Dim d = node.Definition
        If d Is Nothing OrElse Not IsNodePresent(node) Then Return
        Dim startBitLong As Long = If(root, 0L, CLng(baseBit)) + EffectiveOffsetBits(node)
        If startBitLong < 0 OrElse startBitLong > Integer.MaxValue Then Return
        Dim startBit = CInt(startBitLong)
        Select Case d.Kind.ToUpperInvariant()
            Case "FIELD"
                DecodeField(node, source, startBit, limitBytes)
            Case "GROUP", "PAGE"
                For Each child In node.Children
                    DecodeNode(child, source, startBit, False, limitBytes)
                Next
            Case "PAGELIST"
                DecodePageList(node, source, startBit, limitBytes)
        End Select
    End Sub

    Private Shared Sub DecodeField(node As ScsiValueNode, source As Byte(), bitOffset As Integer, limitBytes As Integer)
        Dim d = node.Definition
        If d.FieldType = ScsiFieldType.ASCII Then
            Dim count = If(d.ByteLength > 0, d.ByteLength, (d.BitLength + 7) \ 8)
            If bitOffset Mod 8 <> 0 OrElse bitOffset \ 8 + count > Math.Min(source.Length, limitBytes) Then Return
            node.Value = Encoding.ASCII.GetString(source, bitOffset \ 8, count).TrimEnd(ChrW(0), " "c)
            Return
        ElseIf d.FieldType = ScsiFieldType.Binary Then
            Dim count = If(d.ByteLength > 0, d.ByteLength, (d.BitLength + 7) \ 8)
            If bitOffset Mod 8 <> 0 OrElse bitOffset \ 8 + count > Math.Min(source.Length, limitBytes) Then Return
            node.Value = String.Join(" ", source.Skip(bitOffset \ 8).Take(count).Select(Function(b) b.ToString("X2")))
            Return
        End If
        If bitOffset + d.BitLength > Math.Min(source.Length, limitBytes) * 8 Then Return
        Dim raw = BitCodec.ReadUnsigned(source, bitOffset, d.BitLength)
        If d.IsSigned AndAlso d.BitLength < 64 AndAlso ((raw >> (d.BitLength - 1)) And 1UL) = 1UL Then
            Dim signedBits As ULong = raw Or (ULong.MaxValue << d.BitLength)
            node.Value = BitConverter.ToInt64(BitConverter.GetBytes(signedBits), 0)
        ElseIf d.IsSigned AndAlso d.BitLength = 64 Then
            node.Value = BitConverter.ToInt64(BitConverter.GetBytes(raw), 0)
        Else
            node.Value = raw.ToString(CultureInfo.InvariantCulture)
        End If
    End Sub

    Private Shared Sub DecodePageList(listNode As ScsiValueNode, source As Byte(), startBit As Integer, limitBytes As Integer)
        If startBit Mod 8 <> 0 Then Throw New InvalidOperationException("PageList 必须按字节对齐：" & listNode.Definition.Name)
        Dim cursor = startBit \ 8
        Dim maxBytes = Math.Min(source.Length, limitBytes)
        If listNode.Definition.ByteLength > 0 Then maxBytes = Math.Min(maxBytes, cursor + listNode.Definition.ByteLength)
        Dim allowed = GetAllowedPageTemplates(listNode.Definition, CurrentTemplate)
        If allowed.Count = 0 Then Return
        ' 优先尝试带有 MatchFieldId 的精确匹配模板；没有匹配条件的模板作为兜底，
        ' 避免一个无条件模板排在前面时吞掉后续所有专用模板。
        allowed = allowed.OrderBy(Function(p) If(String.IsNullOrWhiteSpace(p.MatchFieldId), 1, 0)).ToList()
        listNode.Items.Clear()
        Dim guard As Integer = 0
        While cursor < maxBytes AndAlso guard < 4096
            guard += 1
            Dim chosen As ScsiValueNode = Nothing
            Dim chosenSize As Integer = 0
            For Each pageDef In allowed
                Dim candidate = CreateNode(pageDef, CurrentTemplate, listNode)
                Dim minBytes = If(pageDef.ByteLength > 0, pageDef.ByteLength, CInt((ElementExtentBits(candidate) + 7) \ 8))
                If minBytes <= 0 OrElse cursor + minBytes > maxBytes Then
                    ' Still allow variable-length pages with a header shorter than the calculated size.
                    minBytes = Math.Min(maxBytes - cursor, 4)
                End If
                DecodePageHeaderFields(candidate, source, cursor * 8, maxBytes)
                Dim size = DetermineDecodedPageLength(candidate, source, cursor, maxBytes)
                If size <= 0 OrElse cursor + size > maxBytes Then Continue For
                DecodeNode(candidate, source, cursor * 8, False, cursor + size)
                If PageMatches(candidate, pageDef) Then
                    chosen = candidate
                    chosenSize = size
                    Exit For
                End If
            Next
            If chosen Is Nothing OrElse chosenSize <= 0 Then Exit While
            chosen.Parent = listNode
            chosen.InstanceName = chosen.Definition.Name & " [" & (listNode.Items.Count + 1).ToString() & "]"
            listNode.Items.Add(chosen)
            cursor += chosenSize
        End While
    End Sub

    ' Current template is set around public Decode; avoids threading template through every recursive call.
    Private Shared _currentTemplate As ScsiTemplate
    Private Shared ReadOnly Property CurrentTemplate As ScsiTemplate
        Get
            If _currentTemplate Is Nothing Then Throw New InvalidOperationException("解码模板上下文未初始化。")
            Return _currentTemplate
        End Get
    End Property


    Private Shared Sub DecodePageHeaderFields(node As ScsiValueNode, source As Byte(), baseBit As Integer, limitBytes As Integer)
        If node Is Nothing OrElse node.Definition Is Nothing Then Return
        Dim d = node.Definition
        Dim startBit = baseBit + CInt(EffectiveOffsetBits(node))
        If d.Kind.Equals("Field", StringComparison.OrdinalIgnoreCase) Then
            DecodeField(node, source, startBit, limitBytes)
            Return
        End If
        If d.Kind.Equals("PageList", StringComparison.OrdinalIgnoreCase) Then Return
        For Each child In node.Children
            DecodePageHeaderFields(child, source, startBit, limitBytes)
        Next
    End Sub

    Private Shared Function DetermineDecodedPageLength(pageNode As ScsiValueNode, source As Byte(), cursor As Integer, limitBytes As Integer) As Integer
        Dim d = pageNode.Definition
        If d.ByteLength > 0 Then Return d.ByteLength
        If Not String.IsNullOrWhiteSpace(d.LengthFieldId) Then
            Dim lengthNode = Flatten(New List(Of ScsiValueNode) From {pageNode}).FirstOrDefault(Function(n) String.Equals(n.Definition.Id, d.LengthFieldId, StringComparison.OrdinalIgnoreCase))
            If lengthNode IsNot Nothing Then
                Dim value = ParseInteger(lengthNode.Value)
                If value >= 0 AndAlso value <= Integer.MaxValue Then
                    Dim total = CInt(value)
                    If Not d.LengthIncludesHeader Then
                        Dim relativeBits = RelativeBitOffset(pageNode, lengthNode)
                        Dim headerBytes = CInt((relativeBits + ElementExtentBits(lengthNode) + 7) \ 8)
                        total += headerBytes
                    End If
                    Return total
                End If
            End If
        End If
        Return CInt((ElementExtentBits(pageNode) + 7) \ 8)
    End Function

    Private Shared Function RelativeBitOffset(rootNode As ScsiValueNode, targetNode As ScsiValueNode) As Long
        Dim offset As Long = 0
        Dim current = targetNode
        While current IsNot Nothing AndAlso Not Object.ReferenceEquals(current, rootNode)
            If current.Definition IsNot Nothing Then offset += EffectiveOffsetBits(current)
            current = current.Parent
        End While
        If current Is Nothing Then Throw New InvalidOperationException("长度字段不属于 Page 模板：" & targetNode.Definition.Name)
        Return offset
    End Function

    Private Shared Function PageMatches(node As ScsiValueNode, def As ScsiElementDef) As Boolean
        If String.IsNullOrWhiteSpace(def.MatchFieldId) Then Return True
        Dim field = Flatten(New List(Of ScsiValueNode) From {node}).FirstOrDefault(Function(n) String.Equals(n.Definition.Id, def.MatchFieldId, StringComparison.OrdinalIgnoreCase))
        If field Is Nothing Then Return False
        Try
            Return ParseInteger(field.Value) = ParseInteger(def.MatchValue)
        Catch
            Return String.Equals(Convert.ToString(field.Value, CultureInfo.InvariantCulture).Trim(),
                                 def.MatchValue.Trim(), StringComparison.OrdinalIgnoreCase)
        End Try
    End Function

    Private Shared Iterator Function Flatten(nodes As IEnumerable(Of ScsiValueNode)) As IEnumerable(Of ScsiValueNode)
        For Each n In nodes
            Yield n
            For Each c In Flatten(n.Children)
                Yield c
            Next
            For Each item In Flatten(n.Items)
                Yield item
            Next
        Next
    End Function

    Private Shared Function ParseInteger(value As Object) As Long
        Dim s = Convert.ToString(If(value, "0"), CultureInfo.InvariantCulture).Trim()
        If s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) Then Return Convert.ToInt64(s.Substring(2), 16)
        If s = "" Then Return 0
        Return Long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)
    End Function

    Private Shared Function ParseUnsigned(value As Object) As ULong
        Dim s = Convert.ToString(If(value, "0"), CultureInfo.InvariantCulture).Trim()
        If s = "" Then Return 0UL
        If s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) Then
            Return ULong.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        End If
        Return ULong.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)
    End Function

    Private Shared Function ParseHexBytes(value As String) As Byte()
        Dim s = If(value, "").Trim()
        If s = "" Then Return New Byte() {}
        s = Regex.Replace(s, "(?i)0x", "")
        s = Regex.Replace(s, "[,\-:\s]", "")
        If (s.Length Mod 2) <> 0 Then Throw New FormatException("Binary 十六进制字符串必须是偶数个字符。")
        Dim result(s.Length \ 2 - 1) As Byte
        For i As Integer = 0 To result.Length - 1
            result(i) = Byte.Parse(s.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        Next
        Return result
    End Function

    Private Shared Sub EnsureRange(target As Byte(), bitOffset As Integer, bitLength As Integer, fieldName As String)
        If bitOffset < 0 OrElse bitLength < 0 OrElse CLng(bitOffset) + bitLength > CLng(target.Length) * 8L Then
            Throw New InvalidOperationException("字段 " & fieldName & " 超出输出缓冲区。请检查偏移、位宽和长度定义。")
        End If
        If bitOffset Mod 8 <> 0 Then Throw New InvalidOperationException("ASCII/Binary 字段必须按字节对齐：" & fieldName)
    End Sub
End Class
