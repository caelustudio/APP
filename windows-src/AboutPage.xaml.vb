Imports System.Windows
Imports System.Windows.Media

''' <summary>
''' 关于页：作者、感谢名单、应用信息。
''' 头像用的是项目内嵌资源 author-croc.jpg / author-orange.jpg（见 vbproj 的 Resource 项）。
''' </summary>
Class AboutPage

    Public Sub New()
        InitializeComponent()
    End Sub

    Private Sub Page_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        txtAppName.Text = "Caelus Studio"
        txtVersion.Text = AppVersion()
        txtCopyright.Text = "© " & DateTime.Now.Year.ToString() & " Caelus Studio. 保留所有权利。"
    End Sub

    ''' <summary>取程序集版本的主.次.修订三段</summary>
    Private Shared Function AppVersion() As String
        Try
            Dim v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
            If v Is Nothing Then Return "1.2.1"
            Return String.Format("{0}.{1}.{2}", v.Major, v.Minor, v.Build)
        Catch
            Return "1.2.1"
        End Try
    End Function

    Private Sub btnAboutWebsite_Click(sender As Object, e As RoutedEventArgs) Handles btnAboutWebsite.Click
        TreeOSSource.OpenUrl("https://www.caelus.top")
    End Sub

    Private Sub btnAboutGithub_Click(sender As Object, e As RoutedEventArgs) Handles btnAboutGithub.Click
        TreeOSSource.OpenUrl(TreeOSSource.RepositoryUrl)
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
