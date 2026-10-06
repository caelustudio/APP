Imports System.Windows
Imports System.Diagnostics

''' <summary>
''' Star ID 接入配置：填 client_id / 回调地址。保存后由调用方写进配置文件。
''' </summary>
Class StarIdSetupWindow

    ''' <summary>用户填的客户端 ID</summary>
    Public Property ClientId As String = ""

    ''' <summary>登记时使用的回调地址</summary>
    Public Property RedirectUri As String = ""

    ''' <summary>本窗口的圆角半径</summary>
    Private Const WindowCornerRadius As Double = 10

    ''' <summary>
    ''' 尺寸变化后重算裁剪区（SizeToContent 的窗口会先以 0 尺寸布局一次，必须在变化后重算）。
    ''' （只在 XAML 里绑定，VB 侧不要再加 Handles）
    ''' </summary>
    Private Sub RootBorder_SizeChanged(sender As Object, e As SizeChangedEventArgs)
        RoundedWindow.ApplyClip(rootBorder, WindowCornerRadius)
    End Sub

    ''' <summary>用户是否点了保存。调用方看这个，而不是 ShowDialog 的返回值</summary>
    Public Property Saved As Boolean = False

    ' 防止重复关闭：双击按钮时第二次点击事件仍会派发，
    ' 那时窗口已不在模态状态，再设 DialogResult 会抛 InvalidOperationException
    Private _done As Boolean = False

    ''' <summary>
    ''' 收尾。优先用 DialogResult（模态显示时它会顺带关窗），
    ''' 设不了就退化成 Close()，两种情况都不会二次触发。
    ''' </summary>
    Private Sub Finish(result As Boolean)
        If _done Then Return
        _done = True
        Saved = result

        Try
            DialogResult = result
        Catch ex As InvalidOperationException
            ' 不是以对话框方式显示（或已关闭）：退化成普通关闭
            Close()
        End Try
    End Sub

    Private Sub Window_Loaded(sender As Object, e As RoutedEventArgs) Handles MyBase.Loaded
        txtClientId.Text = ClientId
        txtRedirect.Text = RedirectUri

        txtClientId.Focus()
        txtClientId.CaretIndex = txtClientId.Text.Length
    End Sub

    Private Sub btnSave_Click(sender As Object, e As RoutedEventArgs) Handles btnSave.Click
        ' 注意：局部变量别叫 uri / file / path，会遮蔽 System.Uri / IO.File / IO.Path
        Dim clientId = txtClientId.Text.Trim()
        Dim redirect = txtRedirect.Text.Trim()

        If clientId.Length = 0 Then
            MessageBox.Show("请填写客户端 ID。还没登记过的话，点上面的「打开登记页」申请一个。",
                            "Caelus Studio", MessageBoxButton.OK, MessageBoxImage.Information)
            txtClientId.Focus()
            Return
        End If

        If redirect.Length = 0 Then
            redirect = StarIDSource.DefaultRedirectUri
        End If

        Dim parsed As System.Uri = Nothing
        If Not System.Uri.TryCreate(redirect, UriKind.Absolute, parsed) OrElse
           (parsed.Scheme <> "http" AndAlso parsed.Scheme <> "https") Then
            MessageBox.Show("回调地址必须是完整的 http/https 地址。", "Caelus Studio",
                            MessageBoxButton.OK, MessageBoxImage.Information)
            txtRedirect.Focus()
            Return
        End If

        If parsed.Host <> "127.0.0.1" AndAlso parsed.Host <> "localhost" Then
            MessageBox.Show("桌面应用的授权码要回调到本机，地址主机名请用 127.0.0.1 或 localhost。",
                            "Caelus Studio", MessageBoxButton.OK, MessageBoxImage.Information)
            txtRedirect.Focus()
            Return
        End If

        ClientId = clientId
        RedirectUri = redirect
        Finish(True)
    End Sub

    Private Sub btnCancel_Click(sender As Object, e As RoutedEventArgs) Handles btnCancel.Click
        Finish(False)
    End Sub

    Private Sub btnClose_Click(sender As Object, e As RoutedEventArgs) Handles btnClose.Click
        Finish(False)
    End Sub

    Private Sub btnCopyRedirect_Click(sender As Object, e As RoutedEventArgs) Handles btnCopyRedirect.Click
        Try
            Clipboard.SetText(txtRedirect.Text.Trim())
        Catch
        End Try
    End Sub

    ''' <summary>打开终端控制台，去「接入应用 / OAuth 2.0」页登记应用拿 client_id</summary>
    Private Sub btnApply_Click(sender As Object, e As RoutedEventArgs) Handles btnApply.Click
        OpenInBrowser(StarIDSource.ConsoleUrl)
    End Sub

    ''' <summary>打开官方接入文档</summary>
    Private Sub btnDocs_Click(sender As Object, e As RoutedEventArgs) Handles btnDocs.Click
        OpenInBrowser(StarIDSource.DocsUrl)
    End Sub

    ''' <summary>
    ''' 用默认浏览器打开链接。打不开就把地址复制到剪贴板并提示，
    ''' 免得用户卡在这里不知道去哪申请。
    ''' </summary>
    Private Sub OpenInBrowser(url As String)
        ' 注意：局部变量别叫 uri / path，会遮蔽 System.Uri / IO.Path
        Try
            Process.Start(New ProcessStartInfo(url) With {.UseShellExecute = True})
        Catch
            Try
                Clipboard.SetText(url)
            Catch
            End Try
            MessageBox.Show("没能打开浏览器，链接已复制到剪贴板：" & vbCrLf & url,
                            "Caelus Studio", MessageBoxButton.OK, MessageBoxImage.Information)
        End Try
    End Sub

End Class
