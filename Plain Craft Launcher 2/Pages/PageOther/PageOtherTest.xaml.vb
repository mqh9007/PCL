Imports System.Globalization
Imports System.Runtime.InteropServices
Imports System.Security.Cryptography
Imports System.Text.RegularExpressions

Public Class PageOtherTest

    Private HasInitialized As Boolean
    Private LastGeneratedFileName As String = ""
    Private Shared ReadOnly DownloadTargets As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private Shared MemoryRunning As Integer
    Private Shared RubbishRunning As Integer
    Private Shared ReadOnly PresetHints As String() = {
        "在版本设置中启用版本隔离，可以避免不同版本的 Mod 和配置互相影响。",
        "给 Minecraft 分配的内存并不是越多越好，也要给操作系统和其他程序留下足够的内存。",
        "按下 F3 + H 可以显示物品的高级提示信息。",
        "按下 F3 + B 可以显示实体的碰撞箱。",
        "按下 F3 + G 可以显示区块边界。",
        "按下 F2 可以截图，截图会保存在当前游戏目录的 screenshots 文件夹中。",
        "按下 F3 + T 可以重新加载资源包，加载期间游戏可能暂时没有响应。",
        "在 Java 版中，按住 Shift 点击物品可以在容器和背包之间快速移动物品。",
        "安装 Mod 前，请检查它支持的 Minecraft 版本、加载器和前置 Mod。",
        "更新整合包或 Mod 前，记得备份你的存档。",
        "遇到启动问题时，可以在 更多 → 帮助 中查看排查方法。",
        "你可以在设置中调整启动器主页，也可以隐藏不常用的页面。"
    }
    Private Shared ReadOnly PresetCaves As String() = {
        "苦力怕的诞生源于 Notch 在制作猪的模型时弄错了尺寸。",
        "Minecraft 的第一个公开版本发布于 2009 年 5 月 17 日。",
        "末影人的声音是经过处理的人声。",
        "蘑菇岛上的哞菇可以用剪刀剪成普通的牛，但这个过程无法逆转。",
        "用命名牌将羊命名为 jeb_，它的羊毛会不断变色，但剪下来的羊毛仍是原来的颜色。",
        "用命名牌将大多数生物命名为 Dinnerbone 或 Grumm，可以让它们倒立显示。",
        "在 Java 版中，猫和豹猫可以让苦力怕远离自己。",
        "雪傀儡的南瓜头可以用剪刀取下来。"
    }

    Private Sub Page_Loaded(sender As Object, args As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        If HasInitialized Then Return
        HasInitialized = True
        TextDownloadUrl.ValidateRules = New System.Collections.ObjectModel.Collection(Of Validate) From {
            New ValidateNullOrWhiteSpace, New ValidateFunc(AddressOf ValidateDownloadUrl)
        }
        TextDownloadFolder.ValidateRules = New System.Collections.ObjectModel.Collection(Of Validate) From {
            New ValidateFolderPath With {.UseMinecraftCharCheck = False}
        }
        TextDownloadName.ValidateRules = New System.Collections.ObjectModel.Collection(Of Validate) From {
            New ValidateFileName With {.UseMinecraftCharCheck = False}
        }
        Dim Folder = Settings.Get(Of String)("CacheDownloadFolder")
        If String.IsNullOrWhiteSpace(Folder) Then
            Dim DefaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            Folder = CStr(RegistryUtils.TryRead("HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", "{374DE290-123F-4565-9164-39C4925E467B}", DefaultFolder))
            If String.IsNullOrWhiteSpace(Folder) Then Folder = DefaultFolder
        End If
        TextDownloadFolder.Text = PathUtils.AddSlashSuffix(Environment.ExpandEnvironmentVariables(Folder))
        TextDownloadUrl.ForceShowAsSuccess()
        TextDownloadName.ForceShowAsSuccess()
        DownloadInputs_Changed()
    End Sub

    Private Sub BtnDownload_Click() Handles BtnDownload.Click
        DownloadInputs_Changed()
        If Not BtnDownload.IsEnabled Then Return
        Settings.Set("CacheDownloadFolder", TextDownloadFolder.Text.Trim())
        StartCustomDownload(TextDownloadUrl.Text, TextDownloadName.Text, TextDownloadFolder.Text.Trim())
    End Sub

    Private Sub DownloadUrl_Changed() Handles TextDownloadUrl.TextChanged
        If TextDownloadName Is Nothing Then Return
        If TextDownloadName.Text <> "" AndAlso TextDownloadName.Text <> LastGeneratedFileName Then Return
        LastGeneratedFileName = If(ValidateDownloadUrl(TextDownloadUrl.Text) = "", GetDownloadFileName(TextDownloadUrl.Text, Nothing), "")
        TextDownloadName.Text = LastGeneratedFileName
    End Sub

    Private Sub DownloadInputs_Changed() Handles TextDownloadUrl.TextChanged, TextDownloadFolder.TextChanged, TextDownloadName.TextChanged
        If BtnDownload Is Nothing OrElse BtnDownloadOpen Is Nothing OrElse TextDownloadUrl Is Nothing OrElse TextDownloadFolder Is Nothing OrElse TextDownloadName Is Nothing Then Return
        BtnDownload.IsEnabled = ValidateDownloadUrl(TextDownloadUrl.Text) = "" AndAlso TextDownloadFolder.IsValidated AndAlso TextDownloadName.IsValidated
        BtnDownloadOpen.IsEnabled = TextDownloadFolder.IsValidated AndAlso Not String.IsNullOrWhiteSpace(TextDownloadFolder.Text)
    End Sub

    Private Sub BtnDownloadSelect_Click() Handles BtnDownloadSelect.Click
        Dim Folder = Dialogs.SelectFolder("选择下载文件的保存位置", False, TextDownloadFolder.Text).FirstOrDefault()
        If Folder Is Nothing Then Return
        TextDownloadFolder.Text = PathUtils.AddSlashSuffix(Folder)
        Settings.Set("CacheDownloadFolder", TextDownloadFolder.Text)
    End Sub

    Private Sub BtnDownloadOpen_Click() Handles BtnDownloadOpen.Click
        If Not BtnDownloadOpen.IsEnabled Then Return
        Try
            Dim Folder = Path.GetFullPath(TextDownloadFolder.Text.Trim())
            Directory.CreateDirectory(Folder)
            OpenExplorer(PathUtils.AddSlashSuffix(Folder))
        Catch ex As Exception
            Logger.Warn(ex, "打开下载文件夹失败", LogBehavior.Toast)
        End Try
    End Sub

    Private Sub BtnMemory_Click() Handles BtnMemory.Click
        RunInThread(Sub() MemoryOptimize(True))
    End Sub

    Private Sub BtnRubbish_Click() Handles BtnRubbish.Click
        RunInThread(AddressOf RubbishClear)
    End Sub

    Private Sub BtnJrrp_Click() Handles BtnJrrp.Click
        Jrrp()
    End Sub

    Private Sub BtnDontClick_Click() Handles BtnDontClick.Click
        MyMsgBox("不是说了千万别点嘛……" & vbCrLf & vbCrLf & GetRandomHint(), "你还是点了！")
    End Sub

    Private Sub CardCave_Click(sender As Object, args As MouseButtonEventArgs) Handles CardCave.MouseLeftButtonUp
        MyMsgBox(GetRandomCave(), "回声洞")
        args.Handled = True
    End Sub

    Private Sub CardCave_KeyDown(sender As Object, args As KeyEventArgs) Handles CardCave.KeyDown
        If args.Key <> Key.Enter AndAlso args.Key <> Key.Space Then Return
        MyMsgBox(GetRandomCave(), "回声洞")
        args.Handled = True
    End Sub

    Private Shared Function ValidateDownloadUrl(Url As String) As String
        Dim Address As Uri = Nothing
        If String.IsNullOrWhiteSpace(Url) OrElse Not Uri.TryCreate(Url.Trim(), UriKind.Absolute, Address) OrElse
           (Address.Scheme <> Uri.UriSchemeHttp AndAlso Address.Scheme <> Uri.UriSchemeHttps) OrElse String.IsNullOrEmpty(Address.Host) Then
            Return "请输入有效的 HTTP / HTTPS 下载地址！"
        End If
        Return ""
    End Function

    Private Shared Function GetDownloadFileName(Url As String, FileName As String) As String
        If String.IsNullOrWhiteSpace(FileName) OrElse FileName = "未知" OrElse FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 Then
            FileName = Uri.UnescapeDataString(Path.GetFileName(New Uri(Url.Trim()).AbsolutePath))
        End If
        For Each InvalidCharacter In Path.GetInvalidFileNameChars()
            FileName = FileName.Replace(InvalidCharacter, "_"c)
        Next
        FileName = FileName.Trim().TrimEnd("."c)
        If FileName = "" OrElse Regex.IsMatch(FileName, "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)", RegexOptions.IgnoreCase) Then Return "下载文件"
        Return FileName
    End Function

    Public Shared Sub StartCustomDownload(Url As String, FileName As String, Optional Folder As String = Nothing)
        Dim Target As String = Nothing
        Dim Registered As Boolean = False
        Try
            Dim InvalidReason = ValidateDownloadUrl(Url)
            If InvalidReason <> "" Then
                Hint(InvalidReason, HintType.Red)
                Return
            End If
            Url = Url.Trim()
            FileName = GetDownloadFileName(Url, FileName)
            RunInUiWait(
            Sub()
                If String.IsNullOrWhiteSpace(Folder) Then
                    Target = Dialogs.SaveFile("选择保存位置", FileName)
                Else
                    Target = Path.Combine(Path.GetFullPath(Folder), FileName)
                    If File.Exists(Target) AndAlso MyMsgBox($"文件已存在：{Target}{vbCrLf}是否重新下载并覆盖？", "下载文件", "覆盖", "取消", IsWarn:=True) <> 1 Then Target = Nothing
                End If
                If String.IsNullOrWhiteSpace(Target) Then Return
                Target = Path.GetFullPath(Target)
                SyncLock DownloadTargets
                    If Not DownloadTargets.Add(Target) Then
                        Hint("该文件正在下载中！", HintType.Red)
                        Return
                    End If
                    Registered = True
                End SyncLock
                Directory.CreateDirectory(Path.GetDirectoryName(Target))
                Dim Loader As New LoaderCombo(Of String)("文件下载：" & Path.GetFileName(Target), {
                    New LoaderDownload("下载文件", {New NetFile({Url}, Target)})
                }) With {.OnStateChanged =
                Sub(Task)
                    If Task.State = LoadState.Finished OrElse Task.State = LoadState.Failed OrElse Task.State = LoadState.Canceled Then
                        SyncLock DownloadTargets
                            DownloadTargets.Remove(Target)
                        End SyncLock
                    End If
                    LoaderStateChangedHintOnly(Task)
                End Sub}
                Loader.Start(Target)
                LoaderTaskbarAdd(Loader)
                FrmMain.BtnExtraDownload.ShowRefresh()
                FrmMain.BtnExtraDownload.Ribble()
            End Sub)
        Catch ex As Exception
            If Registered Then
                SyncLock DownloadTargets
                    DownloadTargets.Remove(Target)
                End SyncLock
            End If
            Logger.Warn(ex, "启动自定义下载失败", LogBehavior.Toast)
        End Try
    End Sub

    Public Shared Sub Jrrp()
        RunInUi(
        Sub()
            Dim Score As Integer
            Using Hasher = SHA256.Create()
                Dim Hash = Hasher.ComputeHash(Encoding.UTF8.GetBytes(Identify & Date.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                Score = CInt(BitConverter.ToUInt32(Hash, 0) Mod 101UI)
            End Using
            Dim Description = If(Score >= 90, "今天的幸运指数很高！", If(Score >= 60, "今天也是不错的一天！", If(Score >= 30, "平平淡淡也很好。", "别担心，幸运指数只是一个玩笑！")))
            Dim Result = $"今日人品：{Score} / 100{vbCrLf}{Description}{vbCrLf}{vbCrLf}仅供娱乐，请不要用它决定重要的事情。"
            If MyMsgBox(Result, "今日人品", "确定", "复制结果") = 2 Then ClipboardSet(Result)
        End Sub)
    End Sub

    Public Shared Sub RubbishClear()
        If Interlocked.CompareExchange(RubbishRunning, 1, 0) <> 0 Then
            Hint("垃圾清理正在进行中！")
            Return
        End If
        Try
            If McLaunchLoader.State = LoadState.Loading OrElse LoaderTaskbar.Any(Function(Task) Task.State = LoadState.Loading) Then
                Hint("请等待启动或下载任务结束后再清理垃圾！", HintType.Red)
                Return
            End If
            If MyMsgBox("仅清理超过一天的 PCL 下载临时文件与任务缓存，正在使用、无权限访问的文件会被跳过。" & vbCrLf & "不会清理游戏、存档、Mod、启动器设置或其他程序的临时文件。", "清理垃圾", "清理", "取消") <> 1 Then Return
            If McLaunchLoader.State = LoadState.Loading OrElse LoaderTaskbar.Any(Function(Task) Task.State = LoadState.Loading) Then
                Hint("请等待启动或下载任务结束后再清理垃圾！", HintType.Red)
                Return
            End If
            Dim Root = Path.GetFullPath(PathTemp)
            Dim Cutoff = Date.UtcNow.AddDays(-1)
            Dim FreedBytes As Long = 0
            Dim FreedFiles As Integer = 0
            If HasReparsePoint(Root) Then Throw New IOException("缓存路径包含链接，已取消清理以避免影响其他文件夹。")
            Dim DownloadRoot = Path.Combine(Root, "Download")
            If Directory.Exists(DownloadRoot) AndAlso Not HasReparsePoint(DownloadRoot) Then
                For Each FilePath In Directory.GetFiles(DownloadRoot, "*.tmp")
                    If Regex.IsMatch(Path.GetFileName(FilePath), "^\d+_\d+_\d+\.tmp$") Then ClearExpiredFile(FilePath, Cutoff, FreedBytes, FreedFiles)
                Next
            End If
            Dim TaskRoot = Path.Combine(Root, "TaskTemp")
            If Directory.Exists(TaskRoot) AndAlso Not HasReparsePoint(TaskRoot) Then
                For Each FolderPath In Directory.GetDirectories(TaskRoot)
                    If Regex.IsMatch(Path.GetFileName(FolderPath), "^\d+-\d+$") AndAlso Not HasReparsePoint(FolderPath) AndAlso Directory.GetLastWriteTimeUtc(FolderPath) < Cutoff Then
                        ClearExpiredDirectory(FolderPath, Cutoff, FreedBytes, FreedFiles)
                    End If
                Next
            End If
            Hint($"垃圾清理完成，删除了 {FreedFiles} 个文件，释放 {Math.Round(FreedBytes / 1024 / 1024, 1)} MB。", HintType.Green)
        Catch ex As Exception
            Logger.Warn(ex, "垃圾清理失败", LogBehavior.Toast)
        Finally
            Interlocked.Exchange(RubbishRunning, 0)
        End Try
    End Sub

    Private Shared Function HasReparsePoint(Target As String) As Boolean
        Dim Current = Path.GetFullPath(Target)
        Do While Not String.IsNullOrEmpty(Current)
            If (File.GetAttributes(Current) And FileAttributes.ReparsePoint) <> 0 Then Return True
            Current = Path.GetDirectoryName(Current.TrimEnd(Path.DirectorySeparatorChar))
        Loop
        Return False
    End Function

    Private Shared Sub ClearExpiredDirectory(Folder As String, Cutoff As Date, ByRef FreedBytes As Long, ByRef FreedFiles As Integer)
        Try
            If HasReparsePoint(Folder) Then Return
            For Each FilePath In Directory.GetFiles(Folder)
                ClearExpiredFile(FilePath, Cutoff, FreedBytes, FreedFiles)
            Next
            For Each ChildFolder In Directory.GetDirectories(Folder)
                ClearExpiredDirectory(ChildFolder, Cutoff, FreedBytes, FreedFiles)
            Next
            If Not Directory.EnumerateFileSystemEntries(Folder).Any() Then Directory.Delete(Folder, False)
        Catch ex As Exception
            Logger.Trace(ex, $"跳过无法清理的缓存文件夹：{Folder}")
        End Try
    End Sub

    Private Shared Sub ClearExpiredFile(FilePath As String, Cutoff As Date, ByRef FreedBytes As Long, ByRef FreedFiles As Integer)
        Try
            If HasReparsePoint(FilePath) Then Return
            Dim Info As New FileInfo(FilePath)
            If Info.LastWriteTimeUtc >= Cutoff OrElse (Info.Attributes And FileAttributes.ReadOnly) <> 0 Then Return
            Dim Length = Info.Length
            Info.Delete()
            FreedBytes += Length
            FreedFiles += 1
        Catch ex As Exception
            Logger.Trace(ex, $"跳过无法清理的缓存文件：{FilePath}")
        End Try
    End Sub

    Public Shared Sub MemoryOptimize(ShowHint As Boolean)
        If Interlocked.CompareExchange(MemoryRunning, 1, 0) <> 0 Then
            If ShowHint Then Hint("内存优化正在进行中！")
            Return
        End If
        Try
            MemoryOptimizeInternal(ShowHint)
        Catch ex As Exception
            Logger.Warn(ex, "内存优化失败", If(ShowHint, LogBehavior.Toast, LogBehavior.None))
        Finally
            Interlocked.Exchange(MemoryRunning, 0)
        End Try
    End Sub

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function OpenProcess(DesiredAccess As UInteger, InheritHandle As Boolean, ProcessId As Integer) As IntPtr
    End Function

    <DllImport("psapi.dll", SetLastError:=True)>
    Private Shared Function EmptyWorkingSet(ProcessHandle As IntPtr) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function CloseHandle(ProcessHandle As IntPtr) As Boolean
    End Function

    Public Shared Sub MemoryOptimizeInternal(ShowHint As Boolean)
        If ShowHint Then Hint("正在优化内存……")
        Dim Before = My.Computer.Info.AvailablePhysicalMemory
        GC.Collect()
        GC.WaitForPendingFinalizers()
        GC.Collect()
        Dim Optimized As Integer = 0
        For Each TargetProcess In Process.GetProcesses()
            Using TargetProcess
                Dim ProcessHandle = OpenProcess(&H500UI, False, TargetProcess.Id)
                If ProcessHandle = IntPtr.Zero Then Continue For
                Try
                    If EmptyWorkingSet(ProcessHandle) Then Optimized += 1
                Finally
                    CloseHandle(ProcessHandle)
                End Try
            End Using
        Next
        If Optimized = 0 Then Throw New InvalidOperationException("没有可优化的进程，请检查当前用户的权限。")
        Dim After = My.Computer.Info.AvailablePhysicalMemory
        Dim Freed = Math.Max(0, CDbl(After) - CDbl(Before))
        Logger.Info($"内存优化完成，处理了 {Optimized} 个进程，可用物理内存变化 {Math.Round(Freed / 1024 / 1024, 1)} MB")
        If ShowHint Then Hint($"内存优化完成，可用内存增加 {Math.Round(Freed / 1024 / 1024, 1)} MB。", HintType.Green)
    End Sub

    Public Shared Function GetRandomCave() As String
        Return GetRandomText("caves.txt", PresetCaves)
    End Function

    Public Shared Function GetRandomHint() As String
        Return GetRandomText("hints.txt", PresetHints)
    End Function

    Public Shared Function GetRandomPresetHint() As String
        Return PresetHints(RandomInteger(0, PresetHints.Length - 1))
    End Function

    Private Shared Function GetRandomText(FileName As String, Presets As String()) As String
        Try
            Dim CustomPath = Path.Combine(Paths.Base, "PCL", FileName)
            If File.Exists(CustomPath) Then
                Dim Lines = File.ReadAllLines(CustomPath, Encoding.UTF8).Select(Function(Line) Line.Trim()).Where(Function(Line) Line <> "").ToArray()
                If Lines.Length > 0 Then Return Lines(RandomInteger(0, Lines.Length - 1))
            End If
        Catch ex As Exception
            Logger.Trace(ex, $"读取自定义随机内容失败：{FileName}")
        End Try
        Return Presets(RandomInteger(0, Presets.Length - 1))
    End Function

End Class
