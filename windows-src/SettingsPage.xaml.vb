Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media

''' <summary>
''' 设置页：账户（Star ID）与下载位置。
''' 下载位置原来在「下载作品」页，按需求集中到这里；导航栏上的 Star ID 按钮
''' 保留作快捷入口，两边都通过 RefreshStarIdUi() 保持同步。
''' </summary>
Class SettingsPage

    Public Sub New()
        InitializeComponent()
    End Sub

    Private Sub Page_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        RefreshAccount()
        UpdateFolderText()
    End Sub

    ' ---------- 账户 ----------

    Private Sub RefreshAccount()
        Dim user = StarIDSource.CurrentUser
        Dim loggedIn = user IsNot Nothing AndAlso user.StarId.Length > 0

        If loggedIn Then
            txtAccountState.Text = "已登录"
            txtAccountDetail.Text = If(user.Nickname.Length > 0,
                                       user.Nickname & "  ·  " & user.StarId,
                                       user.StarId)
            btnSignIn.Content = "重新登录"
            btnSignOut.IsEnabled = True
            btnCopyId.IsEnabled = True

            ' 把服务端返回的全部字段列出来（账号详情区）
            FillAccountInfo(user)
            pnlAccountInfo.Visibility = Visibility.Visible
        Else
            txtAccountState.Text = "未登录"
            txtAccountDetail.Text = "登录后可以在应用内使用 Caelus Star ID。"
            btnSignIn.Content = "登录 Star ID"
            btnSignOut.IsEnabled = False
            btnCopyId.IsEnabled = False
            pnlAccountInfo.Visibility = Visibility.Collapsed
        End If

        ' 客户端 ID 是应用接入凭据，界面上不展示明文
        txtClientId.Text = "当前暂无权限查看"
        txtClientId.ToolTip = "客户端 ID 由应用内置，界面不展示明文"
        txtRedirect.Text = "当前暂无权限查看"

        ' 让导航栏上的 Star ID 按钮同步
        NotifyShell()
    End Sub

    ''' <summary>把 Star ID 服务端返回的字段逐条填进「账号详情」</summary>
    Private Sub FillAccountInfo(user As StarIDUser)
        If user Is Nothing Then Exit Sub

        txtInfoStarId.Text = If(user.StarId.Length > 0, user.StarId, "（未知）")
        txtInfoNickname.Text = If(user.Nickname.Length > 0, user.Nickname, "（未设置）")

        If user.Email.Length > 0 Then
            txtInfoEmail.Text = user.Email & If(user.EmailVerified, "  ·  已验证", "  ·  未验证")
        Else
            txtInfoEmail.Text = "（未提供）"
        End If

        txtInfoCreated.Text = FormatCreatedAt(user.CreatedAt)
        txtInfoScope.Text = StarIDSource.TokenScope

        Dim exp = StarIDSource.TokenExpiresAt
        txtInfoExpiry.Text = If(exp > DateTime.MinValue, exp.ToString("yyyy-MM-dd HH:mm"), "（未知）")
    End Sub

    ''' <summary>created_at 是毫秒时间戳；没有或解析不出来就显示未知</summary>
    Private Function FormatCreatedAt(ms As Long) As String
        If ms <= 0 Then Return "（未知）"
        Try
            Return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        Catch
            Return "（未知）"
        End Try
    End Function

    ''' <summary>通知 MainWindow 刷新导航栏的 Star ID 状态</summary>
    Private Sub NotifyShell()
        Try
            Dim win = TryCast(Window.GetWindow(Me), MainWindow)
            If win IsNot Nothing Then win.RefreshStarIdUi()
        Catch
        End Try
    End Sub

    Private Async Sub btnSignIn_Click(sender As Object, e As RoutedEventArgs) Handles btnSignIn.Click
        ' client_id 已内置（StarIDSource.DefaultClientId），IsConfigured 恒为 True，
        ' 不再需要「先配置再登录」的分支（1.2.0 起配置入口已整体移除）

        btnSignIn.IsEnabled = False
        btnSignIn.Content = "正在登录…"
        Try
            Dim user = Await StarIDSource.LoginAsync()
            If user Is Nothing Then
                MessageBox.Show("登录已取消或超时。", "Caelus Studio",
                                MessageBoxButton.OK, MessageBoxImage.Information)
            End If
        Catch ex As Exception
            ' 网络类错误真正的线索通常在内层异常里
            Dim detail = ex.Message
            If ex.InnerException IsNot Nothing Then
                detail &= vbCrLf & "（" & ex.InnerException.Message & "）"
            End If
            MessageBox.Show("Star ID 登录失败：" & detail, "Caelus Studio",
                            MessageBoxButton.OK, MessageBoxImage.Warning)
        Finally
            btnSignIn.IsEnabled = True
            RefreshAccount()
        End Try
    End Sub

    Private Async Sub btnSignOut_Click(sender As Object, e As RoutedEventArgs) Handles btnSignOut.Click
        btnSignOut.IsEnabled = False
        Try
            Await StarIDSource.LogoutAsync()
        Catch
        End Try
        RefreshAccount()
    End Sub

    Private Sub btnCopyId_Click(sender As Object, e As RoutedEventArgs) Handles btnCopyId.Click
        If StarIDSource.IsLoggedIn() Then
            Try
                Clipboard.SetText(StarIDSource.CurrentUser.StarId)
            Catch
            End Try
        End If
    End Sub

    ' 「配置客户端 ID…」按钮及其对话框已删：client_id 由应用内置，用户无需配置（1.2.0）

    ' ---------- 下载位置 ----------

    Private Sub UpdateFolderText()
        Dim folder = TreeOSSource.GetDownloadFolder()
        txtFolder.Text = folder
        txtFolder.ToolTip = folder
    End Sub

    Private Sub btnPickFolder_Click(sender As Object, e As RoutedEventArgs) Handles btnPickFolder.Click
        Try
            ' 全限定名，避免 System.Windows.Forms 里的类型与 WPF 的类重名
            Using dlg As New System.Windows.Forms.FolderBrowserDialog()
                dlg.Description = "选择下载存放文件夹"
                dlg.SelectedPath = TreeOSSource.GetDownloadFolder()
                dlg.ShowNewFolderButton = True

                If dlg.ShowDialog() = System.Windows.Forms.DialogResult.OK Then
                    TreeOSSource.SaveDownloadFolder(dlg.SelectedPath)
                    UpdateFolderText()
                End If
            End Using
        Catch ex As Exception
            MessageBox.Show("无法打开文件夹选择：" & ex.Message, "Caelus Studio",
                            MessageBoxButton.OK, MessageBoxImage.Warning)
        End Try
    End Sub

    Private Sub btnOpenFolder_Click(sender As Object, e As RoutedEventArgs) Handles btnOpenFolder.Click
        TreeOSSource.RevealInExplorer(TreeOSSource.GetDownloadFolder())
    End Sub

    Private Sub btnResetFolder_Click(sender As Object, e As RoutedEventArgs) Handles btnResetFolder.Click
        TreeOSSource.SaveDownloadFolder("")
        UpdateFolderText()
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
