Imports System.Windows
Imports System.Windows.Data
Imports System.Globalization

''' <summary>
''' 把进度条的 Value / Maximum / 实际宽度 折算成指示条像素宽度，
''' 让填充实时跟随下载进度（不再依赖框架内部的 Arrange 机制）。
''' </summary>
Public Class ProgressWidthConverter
    Implements IMultiValueConverter

    Public Function Convert(values As Object(),
                            targetType As Type,
                            parameter As Object,
                            culture As CultureInfo) As Object _
        Implements IMultiValueConverter.Convert
        Try
            Dim value As Double = CDbl(values(0))
            Dim maximum As Double = CDbl(values(1))
            Dim actualWidth As Double = CDbl(values(2))
            If maximum <= 0 OrElse actualWidth <= 0 Then Return CDbl(0)
            Dim frac As Double = value / maximum
            If frac < 0 Then frac = 0
            If frac > 1 Then frac = 1
            Return frac * actualWidth
        Catch
            Return CDbl(0)
        End Try
    End Function

    Public Function ConvertBack(value As Object,
                                 targetTypes As Type(),
                                 parameter As Object,
                                 culture As CultureInfo) As Object() _
        Implements IMultiValueConverter.ConvertBack
        Return New Object() {DependencyProperty.UnsetValue,
                             DependencyProperty.UnsetValue,
                             DependencyProperty.UnsetValue}
    End Function
End Class
