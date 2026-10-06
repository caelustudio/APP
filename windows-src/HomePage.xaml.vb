Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media

Class HomePage

    Private Async Sub Page_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        Try
            Dim img = Await TreeOSSource.GetStudioLogoAsync()
            If img IsNot Nothing Then
                brushLogo.ImageSource = img
                hostLogo.Visibility = Visibility.Visible
                txtLogoFallback.Visibility = Visibility.Collapsed
            End If
        Catch
        End Try
    End Sub

    ''' <summary>访问官网按钮改为直接下载安装包</summary>
    Private Sub btnWebsite_Click(sender As Object, e As RoutedEventArgs) Handles btnWebsite.Click
        TreeOSSource.OpenUrl("https://download.caelus.top/Updat/Caelus%20Studio.exe")
    End Sub

    ''' <summary>滚动到「关于我们」区块</summary>
    Private Sub btnAbout_Click(sender As Object, e As RoutedEventArgs) Handles btnAbout.Click
        txtAboutSection.BringIntoView()
    End Sub

    ''' <summary>
    ''' 入场动画结束后必须清掉动画值和 RenderTransform。
    ''' 否则残留的合成层会让整页文字走灰阶抗锯齿，看起来发虚。
    ''' </summary>
    Private Sub EnterAnim_Completed(sender As Object, e As EventArgs)
        Try
            Me.BeginAnimation(OpacityProperty, Nothing)

            Dim t = TryCast(Me.RenderTransform, TranslateTransform)
            If t IsNot Nothing Then
                t.BeginAnimation(TranslateTransform.YProperty, Nothing)
                Me.RenderTransform = Nothing
            End If
        Catch
        End Try
    End Sub

End Class
