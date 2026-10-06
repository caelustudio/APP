Imports System.Windows
Imports System.Threading
Imports System.ComponentModel

''' <summary>
''' 应用更新提示窗口。版本号与说明来自云端管理台，
''' 这里负责展示，并在用户点「立即更新」后**自己完成下载 + 替换 + 重启**，
''' 不再把用户丢到浏览器里手动下载。
''' </summary>
Class UpdateWindow

    ''' <summary>云端的最新版本号</summary>
    Public Property NewVersion As String = ""

    ''' <summary>云端填的更新说明，可能为空</summary>
    Public Property Notes As String = ""

    ''' <summary>本窗口的圆角半径</summary>
    Private Const WindowCornerRadius As Double = 10

    ''' <summary>
    ''' 尺寸变化后重算裁剪区（SizeToContent 的窗口会先以 0 尺寸布局一次，必须在变化后重算）。
    ''' （只在 XAML 里绑定，VB 侧不要再加 Handles）
    ''' </summary>
    Private Sub RootBorder_SizeChanged(sender As Object, e As SizeChangedEventArgs)
        RoundedWindow.ApplyClip(rootBorder, WindowCornerRadius)
    End Sub

    ''' <summary>是否强制更新：强制时隐藏「稍后再说」，改为「退出应用」</summary>
    Public Property IsForced As Boolean = False

    ''' <summary>
    ''' 回退模式：新版本出问题时把客户端拉回旧版本。
    ''' 回退一律按强制处理（不给「稍后再说」），否则坏版本会继续被使用。
    ''' </summary>
    Public Property IsRollback As Boolean = False

    ''' <summary>新版本 exe 的下载地址（云端 app_url / app_rollback_url）</summary>
    Public Property UpdateUrl As String = ""

    ''' <summary>用户点了「立即更新」</summary>
    Public Property ChoseUpdate As Boolean = False

    ''' <summary>用户点了「退出应用」</summary>
    Public Property ChoseExit As Boolean = False

    Private _busy As Boolean = False
    Private _cts As CancellationTokenSource
    Private _progress As Progress(Of DownloadReport)
    Private _status As Progress(Of String)

    Private Sub Window_Loaded(sender As Object, e As RoutedEventArgs) Handles MyBase.Loaded
        txtCurrent.Text = TreeOSSource.LocalAppVersion
        txtNew.Text = If(String.IsNullOrWhiteSpace(NewVersion), "最新版本", NewVersion.Trim())

        If Notes IsNot Nothing AndAlso Notes.Trim().Length > 0 Then
            txtNotes.Text = Notes.Trim()
        Else
            txtNotes.Text = "（本次更新没有填写说明）"
        End If

        If IsRollback Then
            ' 回退：文案全部换掉，且强制（不给「稍后再说」）
            txtTitleBar.Text = "版本回退"
            txtHeading.Text = "当前版本存在问题，需要回退"
            btnUpdate.Content = "立即回退"
            btnLater.Visibility = Visibility.Collapsed
            btnExit.Visibility = Visibility.Visible
            txtForce.Text = "这是必须执行的回退：回退后才能继续使用，也可以直接退出应用。"
            txtForce.Visibility = Visibility.Visible
        ElseIf IsForced Then
            btnLater.Visibility = Visibility.Collapsed
            btnExit.Visibility = Visibility.Visible
            txtForce.Visibility = Visibility.Visible
        Else
            btnLater.Visibility = Visibility.Visible
            btnExit.Visibility = Visibility.Collapsed
            txtForce.Visibility = Visibility.Collapsed
        End If

        ' showContent
        txtProgressTitle.Text = If(IsRollback, "正在下载回退包", "正在下载更新")
    End Sub

    ''' <summary>更新进行中不允许关闭，避免半途退出留下一个坏掉的应用</summary>
    Private Sub Window_Closing(sender As Object, e As CancelEventArgs) Handles Me.Closing
        If Not _busy Then Return
        e.Cancel = True
        MessageBox.Show("更新正在进行中，请先等待完成或点击「取消」。", "Caelus Studio",
                        MessageBoxButton.OK, MessageBoxImage.Information)
    End Sub

    Private Async Sub btnUpdate_Click(sender As Object, e As RoutedEventArgs) Handles btnUpdate.Click
        ChoseUpdate = True
        Await RunUpdateAsync()
    End Sub

    Private Async Sub btnRetry_Click(sender As Object, e As RoutedEventArgs) Handles btnRetry.Click
        Await RunUpdateAsync()
    End Sub

    Private Sub btnCancel_Click(sender As Object, e As RoutedEventArgs) Handles btnCancel.Click
        If _cts IsNot Nothing Then _cts.Cancel()
    End Sub

    Private Sub btnLater_Click(sender As Object, e As RoutedEventArgs) Handles btnLater.Click
        CloseSafely()
    End Sub

    Private Sub btnExit_Click(sender As Object, e As RoutedEventArgs) Handles btnExit.Click
        ChoseExit = True
        CloseSafely()
    End Sub

    Private Sub btnDone_Click(sender As Object, e As RoutedEventArgs) Handles btnDone.Click
        CloseSafely()
    End Sub

    Private Sub btnManual_Click(sender As Object, e As RoutedEventArgs) Handles btnManual.Click
        Dim url = If(UpdateUrl, "").Trim()
        If url.Length = 0 Then Return
        Dim target = AppUpdater.NormalizeUrl(url)
        If target.Length > 0 Then TreeOSSource.OpenUrl(target)
    End Sub

    Private Sub btnClose_Click(sender As Object, e As RoutedEventArgs) Handles btnClose.Click
        ' 标题栏的关闭按钮等同「稍后再说」；强制更新 / 回退时不给逃，直接当作退出
        If _busy Then Return
        If IsForced OrElse IsRollback Then ChoseExit = True
        CloseSafely()
    End Sub

    ' ==================== 执行更新 ====================

    ''' <summary>
    ''' 下载新版 exe 并替换自身。成功进程会被替换并重启，这个方法不会走到最后一步。
    ''' </summary>
    Private Async Function RunUpdateAsync() As Task
        Dim url = If(UpdateUrl, "").Trim()
        If url.Length = 0 Then
            ShowError("管理台还没有填写更新包地址，暂时无法更新。", False)
            Return
        End If

        ' ---- 切到「正在更新」界面 ----
        _busy = True
        txtError.Visibility = Visibility.Collapsed

        panelProgress.Visibility = Visibility.Visible
        txtForce.Visibility = Visibility.Collapsed

        btnUpdate.Visibility = Visibility.Collapsed
        btnLater.Visibility = Visibility.Collapsed
        btnExit.Visibility = Visibility.Collapsed
        btnRetry.Visibility = Visibility.Collapsed
        btnManual.Visibility = Visibility.Collapsed
        btnDone.Visibility = Visibility.Collapsed
        btnCancel.Visibility = Visibility.Visible

        btnClose.IsEnabled = False

        pbDownload.Value = 0
        txtPercent.Text = "0%"
        txtSpeed.Text = ""
        txtStatus.Text = "准备中…"

        _cts = New CancellationTokenSource()
        _progress = New Progress(Of DownloadReport)(AddressOf OnDownloadProgress)
        _status = New Progress(Of String)(AddressOf OnStatus)

        Dim fail As String = ""
        Try
            fail = Await AppUpdater.InstallAsync(url, NewVersion, _progress, _status, _cts.Token)
        Catch ex As Exception
            fail = "更新失败：" & ex.Message
        End Try

        _busy = False
        btnClose.IsEnabled = True
        btnCancel.Visibility = Visibility.Collapsed

        ' 到这里说明没有成功重启（成功时进程已经被替换掉）
        ShowError(If(fail.Length > 0, fail, "更新未能完成，请稍后重试。"), True)
    End Function

    Private Sub ShowError(message As String, allowManual As Boolean)
        txtError.Text = message
        txtError.Visibility = Visibility.Visible

        btnManual.Visibility = If(allowManual, Visibility.Visible, Visibility.Collapsed)
        btnRetry.Visibility = If(allowManual, Visibility.Visible, Visibility.Collapsed)

        ' 强制更新 / 回退：不给「关闭」，只能重试或退出
        If IsForced OrElse IsRollback Then
            btnExit.Visibility = Visibility.Visible
            btnDone.Visibility = Visibility.Collapsed
        Else
            btnDone.Visibility = Visibility.Visible
            btnExit.Visibility = Visibility.Collapsed
        End If
    End Sub

    Private Sub OnDownloadProgress(report As DownloadReport)
        If report Is Nothing Then Return

        If report.Total > 0 Then
            Dim pct = report.Received * 100.0R / report.Total
            If pct > 100.0R Then pct = 100.0R
            pbDownload.Value = pct
            txtPercent.Text = Math.Round(pct, 0).ToString() & "%"
            txtStatus.Text = TreeOSSource.FormatBytes(report.Received) & " / " &
                             TreeOSSource.FormatBytes(report.Total)
        Else
            txtStatus.Text = "已下载 " & TreeOSSource.FormatBytes(report.Received)
        End If

        If report.Speed > 0 Then
            txtSpeed.Text = TreeOSSource.FormatSpeed(report.Speed)
        End If
    End Sub

    Private Sub OnStatus(message As String)
        If String.IsNullOrEmpty(message) Then Return
        txtStatus.Text = message
        If message.StartsWith("正在替换") OrElse message.StartsWith("正在校验") OrElse
           message.StartsWith("正在启动") Then
            pbDownload.IsIndeterminate = False
            pbDownload.Value = 100
            txtPercent.Text = "100%"
            txtSpeed.Text = ""
        End If
    End Sub

    ''' <summary>
    ''' 关窗。DialogResult 只在模态窗口上能设，异常就退回 Close()，
    ''' 免得重复进来时抛 InvalidOperationException。
    ''' </summary>
    Private Sub CloseSafely()
        Try
            DialogResult = True
        Catch ex As InvalidOperationException
            Close()
        End Try
    End Sub

End Class
