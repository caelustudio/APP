Imports System.Text
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Threading

''' <summary>
''' 商店页：展示云端管理台下发的商品。
''' 商品本身不内置在客户端里 —— 增删改、定价、购买链接、是否开放下载，全在管理页配置。
'''
''' 刷新时机（和下载页一致，改完管理台不用重启应用）：
'''   1. 进入本页          —— 强制拉最新
'''   2. 停留在本页        —— 每 30 秒轮询一次
'''   3. 窗口重新获得焦点  —— 由 MainWindow 调 RefreshShopAsync（5 秒节流）
''' </summary>
Class ShopPage

    Private _pollTimer As DispatcherTimer
    Private _lastSync As DateTime = DateTime.MinValue
    ' 上一次渲染出来的商品指纹：内容没变就不重建卡片，
    ' 否则每 30 秒轮询都会把卡片全部换掉，正在进行的下载会被打断
    Private _signature As String = ""

    Private Shared ReadOnly PollInterval As TimeSpan = TimeSpan.FromSeconds(30)
    Private Shared ReadOnly MinSyncInterval As TimeSpan = TimeSpan.FromSeconds(5)

    Private Async Sub Page_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        Await ApplyShopAsync(True)
        StartPolling()
    End Sub

    Private Sub Page_Unloaded(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        StopPolling()
    End Sub

    ' ==================== 实时同步 ====================

    Private Sub StartPolling()
        If _pollTimer IsNot Nothing Then Return
        _pollTimer = New DispatcherTimer()
        _pollTimer.Interval = PollInterval
        AddHandler _pollTimer.Tick, AddressOf OnPollTick
        _pollTimer.Start()
    End Sub

    Private Sub StopPolling()
        If _pollTimer Is Nothing Then Return
        _pollTimer.Stop()
        RemoveHandler _pollTimer.Tick, AddressOf OnPollTick
        _pollTimer = Nothing
    End Sub

    Private Async Sub OnPollTick(sender As Object, e As EventArgs)
        Await ApplyShopAsync(True)
    End Sub

    ''' <summary>
    ''' 供 MainWindow 在窗口重新获得焦点时调用，带节流。
    ''' 在管理页改完商品，切回应用立刻就能看到。
    ''' </summary>
    Public Async Function RefreshShopAsync() As Task
        If (DateTime.Now - _lastSync) < MinSyncInterval Then Return
        Await ApplyShopAsync(True)
    End Function

    Private Async Function ApplyShopAsync(forceRefresh As Boolean) As Task
        Try
            Dim items = Await TreeOSSource.GetShopItemsAsync(forceRefresh)
            RenderItems(items)

            _lastSync = DateTime.Now
            txtSync.Text = "商品已同步 · " & DateTime.Now.ToString("HH:mm:ss")
            txtSync.Visibility = Visibility.Visible
        Catch
            ' 读不到就保留上一次的结果，不把已经显示出来的商品清掉
        End Try
    End Function

    ' ==================== 渲染 ====================

    Private Sub RenderItems(items As List(Of ShopItem))
        If items Is Nothing OrElse items.Count = 0 Then
            ShowEmpty(True)
            Return
        End If

        ' 内容没变化就不动界面
        Dim signature = SignatureOf(items)
        If String.Equals(signature, _signature, StringComparison.Ordinal) Then Return
        _signature = signature

        ShowEmpty(False)
        wrapItems.Children.Clear()

        For Each item In items
            Dim card As New ShopCard()
            card.FallbackLetter = FirstLetter(item.Name)
            wrapItems.Children.Add(card)
            ' 先加入视觉树再喂数据：SetItem 在 Loaded 之前调用，卡片会先存着，
            ' 等自己 Loaded 时再刷界面
            card.SetItem(item)
        Next

        ' 铺满：卡片宽度按当前可用宽度算
        ApplyCardWidth()
    End Sub

    ''' <summary>
    ''' 窗口缩放时重算卡片宽度 —— WrapPanel 不会自己把子项拉宽。
    ''' （只在 VB 这边写 Handles，XAML 里不要再写 SizeChanged，否则处理器会跑两遍）
    ''' </summary>
    Private Sub wrapItems_SizeChanged(sender As Object, e As SizeChangedEventArgs) Handles wrapItems.SizeChanged
        ApplyCardWidth()
    End Sub

    ''' <summary>
    ''' 按可用宽度决定列数：够宽就两列铺满（左右只留一点白），太窄退回一列。
    ''' 卡片自带 16 的右边距，所以每张要扣掉一个间距再均分。
    ''' </summary>
    Private Sub ApplyCardWidth()
        Dim available = wrapItems.ActualWidth
        If available <= 0 Then Return

        Dim columns = If(available >= 760, 2, 1)
        Const gap As Double = 16
        Dim width = (available - gap * columns) / columns
        If width < 220 Then width = 220

        For Each child In wrapItems.Children
            Dim card = TryCast(child, ShopCard)
            If card IsNot Nothing Then card.Width = width
        Next
    End Sub

    Private Sub ShowEmpty(empty As Boolean)
        wrapItems.Visibility = If(empty, Visibility.Collapsed, Visibility.Visible)
        bdEmpty.Visibility = If(empty, Visibility.Visible, Visibility.Collapsed)
        If empty Then
            txtEmptyTitle.Text = If(_signature.Length = 0, "正在读取商品…", "暂时没有上架的商品")
        End If
    End Sub

    ''' <summary>所有会影响界面显示的字段拼成指纹，用于判断要不要重建卡片</summary>
    Private Function SignatureOf(items As List(Of ShopItem)) As String
        Dim sb As New StringBuilder()
        For Each item In items
            sb.Append(item.Id).Append("|")
            sb.Append(item.Name).Append("|")
            sb.Append(item.Tag).Append("|")
            sb.Append(item.Description).Append("|")
            sb.Append(item.PriceText).Append("|")
            sb.Append(item.ImageUrl).Append("|")
            sb.Append(item.BuyUrl).Append("|")
            sb.Append(item.DownloadUrl).Append("|")
            sb.Append(item.DownloadEnabled).Append("|")
            sb.Append(item.Visible).Append(";")
        Next
        Return sb.ToString()
    End Function

    Private Function FirstLetter(name As String) As String
        If String.IsNullOrWhiteSpace(name) Then Return "?"
        Return name.Trim().Substring(0, 1).ToUpperInvariant()
    End Function

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
