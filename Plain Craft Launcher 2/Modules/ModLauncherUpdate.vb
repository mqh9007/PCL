Option Strict On

Imports System.Globalization
Imports System.Reflection
Imports System.Runtime.InteropServices
Imports System.Security.Cryptography
Imports System.Text.RegularExpressions

Friend Module ModLauncherUpdate
    Private Const Repository As String = "mqh9007/PCL"
    '正式发布时由 Actions 注入。
    Private Const CurrentUpdateCode As Integer = 0
    Private Const UpdatePublicKey As String = ""
    Private Const ReleaseBase As String = "https://github.com/" & Repository & "/releases/"
    Private Const ManifestName As String = "update.json"
    Private Const PackageName As String = "PCL.exe"
    Private Const HelperName As String = "Updater.exe"
    Private Const StagingPrefix As String = ".pcl-fork-update-"
    Private Const MaxManifestSize As Integer = 16 * 1024
    Private Const MaxPackageSize As Integer = 100 * 1024 * 1024
    Private Const FailCriticalErrors As UInteger = 1UI

    <DllImport("kernel32.dll")>
    Private Function SetThreadErrorMode(NewMode As UInteger, ByRef OldMode As UInteger) As Boolean
    End Function

    Private ReadOnly Client As New HttpClient(New HttpClientHandler With {.AllowAutoRedirect = False})
    Private CheckRunning As Integer
    Private LatestUpdateCode As Integer
    Private PendingDirectory As String
    Private RestartRequested As Boolean

    Private NotInheritable Class UpdateManifest
        Public VersionCode As Integer
        Public VersionName As String
        Public Sha256 As String
        Public Size As Long
        Public Critical As Boolean
    End Class

    Private ReadOnly Property IsConfigured As Boolean
        Get
            Return BuildType = BuildTypes.Release AndAlso CurrentUpdateCode > 0 AndAlso UpdatePublicKey.Length > 0
        End Get
    End Property

    Friend Function IsForkLauncherNewest() As Boolean?
        Dim Latest = Volatile.Read(LatestUpdateCode)
        If Latest = 0 Then Return Nothing
        Return Latest <= CurrentUpdateCode
    End Function

    Friend Sub StartLauncherUpdateCheck()
        If Not IsConfigured Then Return
        RunInNewThread(AddressOf CleanOldStaging, "清理启动器更新缓存", ThreadPriority.BelowNormal)
        If Settings.Get(Of Integer)("SystemSystemUpdate") <> 3 Then BeginCheck(False)
    End Sub

    Friend Sub UpdateCheckByButton()
        If Not IsConfigured Then
            Hint("此构建未接入正式版更新源。")
            Return
        End If
        BeginCheck(True)
    End Sub

    Private Sub BeginCheck(Manual As Boolean)
        If Interlocked.CompareExchange(CheckRunning, 1, 0) <> 0 Then
            If Manual Then Hint("正在检查或下载启动器更新，请稍候。")
            Return
        End If
        If Manual AndAlso Volatile.Read(PendingDirectory) Is Nothing Then Hint("正在检查启动器更新……")
        RunInNewThread(Sub() CheckForUpdate(Manual), "检查启动器更新", ThreadPriority.BelowNormal)
    End Sub

    Private Sub CheckForUpdate(Manual As Boolean)
        Try
            If Volatile.Read(PendingDirectory) IsNot Nothing Then
                If Manual Then OfferRestart()
                Return
            End If

            Dim ManifestBytes As Byte()
            Using Buffer As New MemoryStream()
                Download(New Uri(ReleaseBase & "latest/download/" & ManifestName), Buffer, MaxManifestSize, Nothing, TimeSpan.FromSeconds(30))
                ManifestBytes = Buffer.ToArray()
            End Using
            Dim Manifest = ReadSignedManifest(ManifestBytes)
            Volatile.Write(LatestUpdateCode, Manifest.VersionCode)
            If Manifest.VersionCode <= CurrentUpdateCode Then
                If Manual Then Hint("当前启动器已是最新版。", HintType.Green)
                Return
            End If

            Dim Mode = Settings.Get(Of Integer)("SystemSystemUpdate")
            If Not Manual AndAlso (Mode = 3 OrElse (Mode = 2 AndAlso Not Manifest.Critical)) Then Return
            If IsProgramEnding Then Return
            If Manual OrElse Mode <> 0 Then
                Dim Accepted = RunInUiWait(Function() MyMsgBox(
                    "发现新版本 " & Manifest.VersionName & "（构建 " & Manifest.VersionCode.ToString(CultureInfo.InvariantCulture) & "）。",
                    "启动器更新", "下载更新", "稍后") = 1)
                If Not Accepted OrElse IsProgramEnding Then Return
            End If

            Hint("正在下载启动器更新……")
            StagePackage(Manifest, ManifestBytes)
            If Manual Then
                OfferRestart()
            Else
                Hint("启动器更新已下载，将在退出时安装。", HintType.Green)
            End If
        Catch ex As Exception
            Logger.Warn(ex, "启动器更新失败")
            If Manual Then Hint("启动器更新失败：" & ex.Message, HintType.Red)
        Finally
            Interlocked.Exchange(CheckRunning, 0)
        End Try
    End Sub

    Private Sub OfferRestart()
        RunInUiWait(
            Sub()
                If IsProgramEnding OrElse FrmMain Is Nothing Then Return
                If MyMsgBox("更新已下载。你可以现在重启 PCL，也可以在退出时安装。", "启动器更新", "立即重启", "退出时安装") = 1 Then
                    FrmMain.EndProgram(True, RestartForUpdate:=True)
                End If
            End Sub)
    End Sub

    Friend Sub RestartAfterLauncherUpdate()
        RestartRequested = True
    End Sub

    Private Function ReadSignedManifest(ManifestBytes As Byte()) As UpdateManifest
        If ManifestBytes.Length > MaxManifestSize Then Throw New InvalidDataException("更新清单过大。")
        Dim Envelope = JObject.Parse(Encoding.UTF8.GetString(ManifestBytes))
        Dim Payload = Convert.FromBase64String(Envelope.Value(Of String)("payload"))
        Dim Signature = Convert.FromBase64String(Envelope.Value(Of String)("signature"))
        Dim KeyParts = UpdatePublicKey.Split("."c)
        Using Verifier = RSA.Create()
            Verifier.ImportParameters(New RSAParameters With {
                .Modulus = Convert.FromBase64String(KeyParts(0)),
                .Exponent = Convert.FromBase64String(KeyParts(1))
            })
            If Not Verifier.VerifyData(Payload, Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1) Then
                Throw New InvalidDataException("更新清单签名无效。")
            End If
        End Using

        Dim Data = JObject.Parse(Encoding.UTF8.GetString(Payload))
        Dim Result As New UpdateManifest With {
            .VersionCode = Data.Value(Of Integer)("versionCode"),
            .VersionName = Data.Value(Of String)("versionName"),
            .Sha256 = Data.Value(Of String)("sha256"),
            .Size = Data.Value(Of Long)("size"),
            .Critical = Data.Value(Of Boolean)("critical")
        }
        If Not String.Equals(Data.Value(Of String)("repository"), Repository, StringComparison.OrdinalIgnoreCase) OrElse Result.VersionCode <= 0 OrElse
           String.IsNullOrWhiteSpace(Result.VersionName) OrElse Result.VersionName.Length > 80 OrElse
           Not Regex.IsMatch(If(Result.Sha256, ""), "\A[0-9a-fA-F]{64}\z") OrElse
           Result.Size <= 0 OrElse Result.Size > MaxPackageSize Then
            Throw New InvalidDataException("更新清单内容无效。")
        End If
        Return Result
    End Function

    Private Sub Download(Address As Uri, Output As Stream, Limit As Long, ExpectedSize As Long?, Timeout As TimeSpan)
        Using Deadline As New CancellationTokenSource(Timeout)
            For Hop As Integer = 0 To 4
                If Address.Scheme <> Uri.UriSchemeHttps OrElse Not Address.IsDefaultPort OrElse Address.UserInfo.Length <> 0 OrElse
                   (Address.Host <> "github.com" AndAlso Address.Host <> "release-assets.githubusercontent.com" AndAlso
                    Address.Host <> "objects.githubusercontent.com") Then
                    Throw New InvalidDataException("更新下载地址无效。")
                End If
                Using Response = Client.GetAsync(Address, HttpCompletionOption.ResponseHeadersRead, Deadline.Token).GetAwaiter().GetResult()
                    Select Case CInt(Response.StatusCode)
                        Case 301, 302, 303, 307, 308
                            If Response.Headers.Location Is Nothing Then Throw New InvalidDataException("更新下载缺少重定向地址。")
                            Address = New Uri(Address, Response.Headers.Location)
                            Continue For
                    End Select
                    Response.EnsureSuccessStatusCode()
                    If Response.Content.Headers.ContentLength.GetValueOrDefault() > Limit Then Throw New InvalidDataException("更新文件大小无效。")
                    Using Input = Response.Content.ReadAsStreamAsync().GetAwaiter().GetResult(),
                          CancelRead = Deadline.Token.Register(Sub() Response.Dispose())
                        Dim Buffer(81919) As Byte
                        Dim Total As Long = 0
                        While True
                            Dim Count = Input.ReadAsync(Buffer, 0, Buffer.Length, Deadline.Token).GetAwaiter().GetResult()
                            If Count = 0 Then Exit While
                            Total += Count
                            If Total > Limit Then Throw New InvalidDataException("更新文件大小无效。")
                            Output.Write(Buffer, 0, Count)
                        End While
                        If ExpectedSize.HasValue AndAlso Total <> ExpectedSize.Value Then Throw New InvalidDataException("更新文件不完整。")
                    End Using
                    Return
                End Using
            Next
        End Using
        Throw New InvalidDataException("更新下载重定向次数过多。")
    End Sub

    Private Sub StagePackage(Manifest As UpdateManifest, ManifestBytes As Byte())
        Dim Stage = Path.Combine(Paths.Base, StagingPrefix & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(Stage)
        Try
            Dim Package = Path.Combine(Stage, PackageName)
            Dim Address As New Uri(ReleaseBase & "download/pcl-v" & Manifest.VersionCode.ToString(CultureInfo.InvariantCulture) & "/" & PackageName)
            Using Output As New FileStream(Package, FileMode.CreateNew, FileAccess.Write)
                Download(Address, Output, Manifest.Size, Manifest.Size, TimeSpan.FromMinutes(3))
            End Using
            VerifyPackage(Package, Manifest)
            File.WriteAllBytes(Path.Combine(Stage, ManifestName), ManifestBytes)
            File.Copy(PathExe, Path.Combine(Stage, HelperName))
            Volatile.Write(PendingDirectory, Stage)
        Catch
            CleanStage(Stage)
            Throw
        End Try
    End Sub

    Friend Sub InstallLauncherUpdateOnExit()
        Dim Stage = Interlocked.Exchange(PendingDirectory, Nothing)
        If Stage Is Nothing Then Return
        Try
            Using Current = Process.GetCurrentProcess()
                Dim Arguments = "--update " & Current.Id.ToString(CultureInfo.InvariantCulture) & " " &
                    Current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) & " " &
                    If(RestartRequested, "1", "0") & " """ & Path.GetFileName(PathExe) & """"
                Using Helper = StartProcess(New ProcessStartInfo With {
                    .FileName = Path.Combine(Stage, HelperName), .Arguments = Arguments, .UseShellExecute = False
                })
                End Using
            End Using
        Catch ex As Exception
            Logger.Warn(ex, "无法启动更新安装程序")
            MessageBox.Show("无法启动更新安装程序：" & ex.Message, "启动器更新")
        End Try
    End Sub

    Friend Function ReplaceLauncher(Arguments As String()) As Boolean
        Dim Target As String = Nothing
        Dim Backup As String = Nothing
        Dim Restart = False
        Dim HasValidatedTarget = False
        Dim ReplaceStarted = False
        Dim Installed = False
        Try
            Dim ParentId As Integer
            Dim ParentStart As Long
            If Not IsConfigured OrElse Arguments.Length <> 5 OrElse
               Not Integer.TryParse(Arguments(1), NumberStyles.None, CultureInfo.InvariantCulture, ParentId) OrElse ParentId <= 0 OrElse
               Not Long.TryParse(Arguments(2), NumberStyles.None, CultureInfo.InvariantCulture, ParentStart) OrElse ParentStart <= 0 OrElse
               (Arguments(3) <> "0" AndAlso Arguments(3) <> "1") OrElse
               Arguments(4).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 OrElse
               Not Arguments(4).EndsWith(".exe", StringComparison.OrdinalIgnoreCase) Then
                Throw New InvalidDataException("更新参数无效。")
            End If

            Dim HelperPath = Assembly.GetExecutingAssembly().Location
            Dim Stage = New DirectoryInfo(Path.GetDirectoryName(HelperPath))
            If Not String.Equals(Path.GetFileName(HelperPath), HelperName, StringComparison.OrdinalIgnoreCase) OrElse
               Not IsStagingFolder(Stage) OrElse Stage.Parent Is Nothing Then Throw New InvalidDataException("更新目录无效。")
            Target = Path.Combine(Stage.Parent.FullName, Arguments(4))
            Backup = Target & ".pcl-update.bak"
            Dim Package = Path.Combine(Stage.FullName, PackageName)
            Dim ManifestPath = Path.Combine(Stage.FullName, ManifestName)
            If New FileInfo(ManifestPath).Length > MaxManifestSize Then Throw New InvalidDataException("更新清单过大。")
            Dim Manifest = ReadSignedManifest(File.ReadAllBytes(ManifestPath))
            If Manifest.VersionCode <= CurrentUpdateCode Then Throw New InvalidDataException("更新版本号无效。")
            WaitForLauncherExit(ParentId, ParentStart, Target)
            If IsReparsePoint(Target) OrElse (File.Exists(Backup) AndAlso IsReparsePoint(Backup)) OrElse
               GetSha256(HelperPath) <> GetSha256(Target) Then Throw New InvalidDataException("待更新的启动器与当前构建不匹配。")
            HasValidatedTarget = True
            Restart = Arguments(3) = "1"
            VerifyPackage(Package, Manifest)
            ReplaceStarted = True
            File.Replace(Package, Target, Backup)
            Installed = True
            If Restart Then Relaunch(Target)
            Return True
        Catch ex As Exception
            Dim Detail = ex.Message
            Try
                If Installed Then
                    File.Replace(Backup, Target, Nothing)
                ElseIf ReplaceStarted AndAlso Not File.Exists(Target) AndAlso File.Exists(Backup) Then
                    If GetSha256(Backup) = GetSha256(Assembly.GetExecutingAssembly().Location) Then File.Move(Backup, Target)
                End If
                If Restart AndAlso HasValidatedTarget Then Relaunch(Target)
            Catch RecoveryError As Exception
                Detail &= vbCrLf & "恢复启动器时出错：" & RecoveryError.Message & vbCrLf & "备份路径：" & Backup
            End Try
            MessageBox.Show("安装启动器更新失败：" & Detail, "启动器更新")
            Return False
        End Try
    End Function

    Private Sub WaitForLauncherExit(ParentId As Integer, ParentStart As Long, Target As String)
        Dim Parent As Process
        Try
            Parent = Process.GetProcessById(ParentId)
        Catch ex As ArgumentException
            Return '旧进程已退出。
        End Try
        Using Parent
            Try
                If Parent.StartTime.ToUniversalTime().Ticks <> ParentStart Then Return '进程 ID 已被复用。
                If Not String.Equals(Parent.MainModule.FileName, Target, StringComparison.OrdinalIgnoreCase) Then
                    Throw New InvalidDataException("启动更新的进程与安装目标不匹配。")
                End If
                If Not Parent.WaitForExit(30000) Then Throw New TimeoutException("等待旧启动器退出超时。")
            Catch ex As Exception When (TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ComponentModel.Win32Exception) AndAlso Parent.HasExited
                '旧进程在读取信息时退出。
            End Try
        End Using
    End Sub

    Private Sub Relaunch(Target As String)
        Dim OldMode As UInteger
        Dim RestoreMode = SetThreadErrorMode(FailCriticalErrors, OldMode)
        Try
            Using Launcher = Process.Start(New ProcessStartInfo With {
                .FileName = Target, .WorkingDirectory = Path.GetDirectoryName(Target), .UseShellExecute = False
            })
            End Using
        Finally
            If RestoreMode Then
                Dim IgnoredMode As UInteger
                SetThreadErrorMode(OldMode, IgnoredMode)
            End If
        End Try
    End Sub

    Private Sub VerifyPackage(Package As String, Manifest As UpdateManifest)
        If IsReparsePoint(Package) OrElse New FileInfo(Package).Length <> Manifest.Size OrElse
           Not String.Equals(GetSha256(Package), Manifest.Sha256, StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidDataException("更新文件校验失败。")
        End If
        If AssemblyName.GetAssemblyName(Package).Name <> Assembly.GetExecutingAssembly().GetName().Name Then
            Throw New InvalidDataException("更新文件不是此启动器的可执行程序。")
        End If
    End Sub

    Private Function GetSha256(FilePath As String) As String
        Using Input = File.OpenRead(FilePath), Hasher = SHA256.Create()
            Return BitConverter.ToString(Hasher.ComputeHash(Input)).Replace("-", "")
        End Using
    End Function

    Private Function IsReparsePoint(FilePath As String) As Boolean
        Return (File.GetAttributes(FilePath) And FileAttributes.ReparsePoint) <> 0
    End Function

    Private Function IsStagingFolder(Folder As DirectoryInfo) As Boolean
        Dim Id As Guid
        Return Folder.Name.StartsWith(StagingPrefix, StringComparison.Ordinal) AndAlso
            Guid.TryParseExact(Folder.Name.Substring(StagingPrefix.Length), "N", Id) AndAlso
            (Folder.Attributes And FileAttributes.ReparsePoint) = 0
    End Function

    Private Sub CleanOldStaging()
        For Each Stage In New DirectoryInfo(Paths.Base).EnumerateDirectories(StagingPrefix & "*")
            If IsStagingFolder(Stage) AndAlso Stage.CreationTimeUtc < Date.UtcNow.AddDays(-1) Then CleanStage(Stage.FullName)
        Next
    End Sub

    Private Sub CleanStage(Stage As String)
        Try
            If Not IsStagingFolder(New DirectoryInfo(Stage)) Then Return
            File.Delete(Path.Combine(Stage, HelperName))
            File.Delete(Path.Combine(Stage, PackageName))
            File.Delete(Path.Combine(Stage, ManifestName))
            Directory.Delete(Stage)
        Catch ex As IOException
        Catch ex As UnauthorizedAccessException
        End Try
    End Sub
End Module
