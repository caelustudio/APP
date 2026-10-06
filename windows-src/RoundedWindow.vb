Imports System
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media

''' <summary>
''' 圆角窗口的收尾工作。
'''
''' 光给最外层 Border 设 CornerRadius 是不够的：CornerRadius 只影响 Border 自己
''' 画背景/描边的形状，**子元素仍然是方形的**，会从四个圆角处顶出来，看起来就是「白底方角 + 圆角边框」。
''' 必须再显式套一个圆角矩形裁剪（Clip），把子元素一起裁掉。
'''
''' 另外，AllowsTransparency=True 时窗口整体参与分层合成，窗口尺寸变化时 Clip 的尺寸
''' 不会自动跟着变，所以要在 SizeChanged 里重新算一遍。
''' </summary>
Public Module RoundedWindow

    ''' <summary>
    ''' 给窗口最外层 Border 套上圆角裁剪。
    ''' radius 传 0 表示取消圆角（最大化时用），会自动清掉 Clip。
    ''' </summary>
    Public Sub ApplyClip(border As Border, radius As Double)
        If border Is Nothing Then Return

        Try
            Dim w = border.ActualWidth
            Dim h = border.ActualHeight

            If w <= 0 OrElse h <= 0 Then
                border.Clip = Nothing
                Return
            End If

            Dim r = radius
            If r < 0 Then r = 0

            ' 圆角半径不能超过短边的一半，否则形状会畸变
            Dim max = Math.Min(w, h) / 2
            If r > max Then r = max

            If r <= 0 Then
                border.Clip = Nothing
                border.CornerRadius = New CornerRadius(0)
            Else
                border.Clip = New RectangleGeometry(New Rect(0, 0, w, h), r, r)
                border.CornerRadius = New CornerRadius(r)
            End If
        Catch
        End Try
    End Sub

End Module
