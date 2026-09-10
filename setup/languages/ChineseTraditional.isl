; *** Inno Setup version 6.5.0+ Chinese Simplified messages ***
;
; To download user-contributed translations of this file, go to:
;   https://jrsoftware.org/files/istrans/
;
; Note: When translating this text, do not add periods (.) to the end of
; messages that didn't have them already, because on those messages Inno
; Setup adds the periods automatically (appending a period would result in
; two periods being displayed).
;
; Maintainer: Zhenghan Yang (Kira)
; Email: 847320916@QQ.com
; Github: https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation
; Encoding: UTF-8
; Translation based on network resource
;

[LangOptions]
; The following three entries are very important. Be sure to read and
; understand the '[LangOptions] section' topic in the help file.
LanguageName=繁體中文
; About LanguageID, to reference link:
; https://docs.microsoft.com/en-us/openspecs/windows_protocols/ms-lcid/a9eac961-e77d-41a6-90a5-ce1a8b0cdb9c
LanguageID=$0404
; LanguageCodePage should always be set if possible, even if this file is Unicode
; For English it's set to zero anyway because English only uses ASCII characters
LanguageCodePage=950
; If the language you are translating to requires special font faces or
; sizes, uncomment any of the following entries and change them accordingly.
;DialogFontName=
;DialogFontSize=9
;DialogFontBaseScaleWidth=7
;DialogFontBaseScaleHeight=15
;WelcomeFontName=Segoe UI
;WelcomeFontSize=14

[Messages]

; *** Application titles
SetupAppTitle=安裝
SetupWindowTitle=安裝 - %1
UninstallAppTitle=解除安裝
UninstallAppFullTitle=%1 解除安裝

; *** Misc. common
InformationTitle=資訊
ConfirmTitle=確認
ErrorTitle=錯誤

; *** SetupLdr messages
SetupLdrStartupMessage=現在將安裝 %1。您想要繼續嗎？
LdrCannotCreateTemp=无法建立临時檔案。安裝程式已中止
LdrCannotExecTemp=无法执行临時目錄中的檔案。安裝程式已中止
HelpTextNote=

; *** Startup error messages
LastErrorMessage=%1。%n%n錯誤 %2: %3
SetupFileMissing=安裝目錄中缺少檔案 %1。請修正這個問題或者获取程式的新副本。
SetupFileCorrupt=安裝檔案已损坏。請获取程式的新副本。
SetupFileCorruptOrWrongVer=安裝檔案已损坏，或是與這個安裝程式的版本不兼容。請修正這個問題或获取新的程式副本。
InvalidParameter=无效的命令行参数：%n%n%1
SetupAlreadyRunning=安裝程式已在執行。
WindowsVersionNotSupported=此程式不支持当前電腦執行的 Windows 版本。
WindowsServicePackRequired=此程式需要 %1 服务包 %2 或更高版本。
NotOnThisPlatform=此程式不能在 %1 上執行。
OnlyOnThisPlatform=此程式只能在 %1 上執行。
OnlyOnTheseArchitectures=此程式只能安裝到為下列处理器架构設计的 Windows 版本中：%n%n%1
WinVersionTooLowError=此程式需要 %1 版本 %2 或更高。
WinVersionTooHighError=此程式不能安裝于 %1 版本 %2 或更高。
AdminPrivilegesRequired=在安裝此程式時您必須以管理员身份登录。
PowerUserPrivilegesRequired=在安裝此程式時您必須以管理员身份或高级使用者组身份登录。
SetupAppRunningError=安裝程式檢測到 %1 当前正在執行。%n%n請先關閉正在執行的程式，然後點擊“確定”繼續，或點擊“取消”退出。
UninstallAppRunningError=解除安裝程式檢測到 %1 当前正在執行。%n%n請先關閉正在執行的程式，然後點擊“確定”繼續，或點擊“取消”退出。

; *** Startup questions
PrivilegesRequiredOverrideTitle=選擇安裝程式安裝模式
PrivilegesRequiredOverrideInstruction=選擇安裝模式
PrivilegesRequiredOverrideText1=%1 可以為所有使用者安裝（需要管理员权限），或僅為您安裝。
PrivilegesRequiredOverrideText2=%1 可以僅為您安裝，或為所有使用者安裝（需要管理员权限）。
PrivilegesRequiredOverrideAllUsers=為所有使用者安裝(&A)
PrivilegesRequiredOverrideAllUsersRecommended=為所有使用者安裝(&A)（推荐）
PrivilegesRequiredOverrideCurrentUser=僅為我安裝(&M)
PrivilegesRequiredOverrideCurrentUserRecommended=僅為我安裝(&M)（推荐）

; *** Misc. errors
ErrorCreatingDir=安裝程式无法建立目錄“%1”
ErrorTooManyFilesInDir=无法在目錄“%1”中建立檔案，因為裡面包含太多檔案

; *** Setup common messages
ExitSetupTitle=退出安裝程式
ExitSetupMessage=安裝程式尚未完成。如果现在退出，將不會安裝该程式。%n%n您之後可以再次執行安裝程式完成安裝。%n%n现在退出安裝程式嗎？
AboutSetupMenuItem=关于安裝程式(&A)...
AboutSetupTitle=关于安裝程式
AboutSetupMessage=%1 版本 %2%n%3%n%n%1 主頁：%n%4
AboutSetupNote=
TranslatorNote=繁體中文翻译由 Kira（847320916@qq.com）维护。項目地址：https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation

; *** Buttons
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安裝(&I)
ButtonOK=確定
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonYesToAll=全是(&A)
ButtonNo=否(&N)
ButtonNoToAll=全否(&O)
ButtonFinish=完成(&F)
ButtonBrowse=瀏覽(&B)...
ButtonWizardBrowse=瀏覽(&R)...
ButtonNewFolder=新建資料夾(&M)

; *** "Select Language" dialog messages
SelectLanguageTitle=選擇安裝語言
SelectLanguageLabel=選擇安裝時使用的語言。

; *** Common wizard text
ClickNext=點擊“下一步”繼續，或點擊“取消”退出安裝程式。
BeveledLabel=
BrowseDialogTitle=瀏覽資料夾
BrowseDialogLabel=在下面的列表中選擇一個資料夾，然後點擊“確定”。
NewFolderName=新建資料夾

; *** "Welcome" wizard page
WelcomeLabel1=欢迎使用 [name] 安裝精靈
WelcomeLabel2=即將在您的電腦上安裝 [name/ver]。%n%n建議您在繼續安裝前關閉所有其他應用程式。

; *** "Password" wizard page
WizardPassword=密碼
PasswordLabel1=此安裝程式需要密碼驗證。
PasswordLabel3=請輸入密碼，然後點擊“下一步”繼續。密碼区分大小写。
PasswordEditLabel=密碼(&P)：
IncorrectPassword=您輸入的密碼不正确，請重新輸入。

; *** "License Agreement" wizard page
WizardLicense=授權合約
LicenseLabel=請在繼續安裝前閱讀以下重要資訊。
LicenseLabel3=請閱讀下列授權合約。在繼續安裝前您必須同意這些合約条款。
LicenseAccepted=我同意此合約(&A)
LicenseNotAccepted=我不同意此合約(&D)

; *** "Information" wizard pages
WizardInfoBefore=資訊
InfoBeforeLabel=請在繼續安裝前閱讀以下重要資訊。
InfoBeforeClickLabel=準備好繼續安裝後，點擊“下一步”。
WizardInfoAfter=資訊
InfoAfterLabel=請在繼續安裝前閱讀以下重要資訊。
InfoAfterClickLabel=準備好繼續安裝後，點擊“下一步”。

; *** "User Information" wizard page
WizardUserInfo=使用者資訊
UserInfoDesc=請輸入您的資訊。
UserInfoName=使用者名(&U)：
UserInfoOrg=組織(&O)：
UserInfoSerial=序列号(&S)：
UserInfoNameRequired=請輸入使用者名。

; *** "Select Destination Location" wizard page
WizardSelectDir=選擇目標位置
SelectDirDesc=您想將 [name] 安裝在哪裡？
SelectDirLabel3=安裝程式將安裝 [name] 到下面的資料夾中。
SelectDirBrowseLabel=點擊“下一步”繼續。如果您想選擇其他資料夾，點擊“瀏覽”。
DiskSpaceGBLabel=至少需要有 [gb] GB 的可用磁碟空間。
DiskSpaceMBLabel=至少需要有 [mb] MB 的可用磁碟空間。
CannotInstallToNetworkDrive=安裝程式无法安裝到一個網路磁碟機。
CannotInstallToUNCPath=安裝程式无法安裝到一個 UNC 路径。
InvalidPath=您必須輸入一個带磁碟機盘符的完整路径，例如：%n%nC:\App%n%n或UNC路径：%n%n\\server\share
InvalidDrive=您選定的磁碟機或 UNC 共享不存在或不能訪問。請選擇其他位置。
DiskSpaceWarningTitle=磁碟空間不足
DiskSpaceWarning=安裝程式至少需要 %1 KB 的可用空間才能安裝，但選定磁碟機只有 %2 KB 的可用空間。%n%n您確定要繼續嗎？
DirNameTooLong=資料夾名称或路径太長。
InvalidDirName=資料夾名称无效。
BadDirName32=資料夾名称不能包含下列任何字符：%n%n%1
DirExistsTitle=資料夾已存在
DirExists=資料夾：%n%n%1%n%n已经存在。您確定安裝到這個資料夾中嗎？
DirDoesntExistTitle=資料夾不存在
DirDoesntExist=資料夾：%n%n%1%n%n不存在。您想要建立此資料夾嗎？

; *** "Select Components" wizard page
WizardSelectComponents=選擇元件
SelectComponentsDesc=您想安裝哪些程式元件？
SelectComponentsLabel2=選取您想安裝的元件；取消您不想安裝的元件。然後點擊“下一步”繼續。
FullInstallation=完全安裝
; if possible don't translate 'Compact' as 'Minimal' (I mean 'Minimal' in your language)
CompactInstallation=简洁安裝
CustomInstallation=自定义安裝
NoUninstallWarningTitle=元件已存在
NoUninstallWarning=安裝程式檢測到下列元件已安裝在您的電腦中：%n%n%1%n%n取消選取這些元件不會解除安裝它們。%n%n您確定要繼續嗎？
ComponentSize1=%1 KB
ComponentSize2=%1 MB
ComponentsDiskSpaceGBLabel=当前選擇的元件需要至少 [gb] GB 的磁碟空間。
ComponentsDiskSpaceMBLabel=当前選擇的元件需要至少 [mb] MB 的磁碟空間。

; *** "Select Additional Tasks" wizard page
WizardSelectTasks=選擇附加工作
SelectTasksDesc=您想要安裝程式执行哪些附加工作？
SelectTasksLabel2=選擇您想要安裝程式在安裝 [name] 時执行的附加工作，然後點擊“下一步”。

; *** "Select Start Menu Folder" wizard page
WizardSelectProgramGroup=選擇開始功能表資料夾
SelectStartMenuFolderDesc=安裝程式应该在哪裡放置程式的捷徑？
SelectStartMenuFolderLabel3=安裝程式將在下列“开始”菜单資料夾中建立程式的捷徑。
SelectStartMenuFolderBrowseLabel=點擊“下一步”繼續。如果您想選擇其他資料夾，點擊“瀏覽”。
MustEnterGroupName=您必須輸入一個資料夾名称。
GroupNameTooLong=資料夾名称或路径太長。
InvalidGroupName=資料夾名称无效。
BadGroupName=資料夾名称不能包含下列任何字符：%n%n%1
NoProgramGroupCheck2=不建立開始功能表資料夾(&D)

; *** "Ready to Install" wizard page
WizardReady=準備安裝
ReadyLabel1=安裝程式準備就绪，现在可以开始安裝 [name] 到您的電腦。
ReadyLabel2a=點擊“安裝”繼續此安裝程式。如果您想重新查看或修改任何設定，點擊“上一步”。
ReadyLabel2b=點擊“安裝”繼續此安裝程式。
ReadyMemoUserInfo=使用者資訊：
ReadyMemoDir=目標位置：
ReadyMemoType=安裝類型：
ReadyMemoComponents=已選擇元件：
ReadyMemoGroup=開始功能表資料夾：
ReadyMemoTasks=附加工作：

; *** TDownloadWizardPage wizard page and DownloadTemporaryFile
DownloadingLabel2=正在下載檔案...
ButtonStopDownload=停止下載(&S)
StopDownload=您確定要停止下載嗎？
ErrorDownloadAborted=下載已中止
ErrorDownloadFailed=下載失敗：%1 %2
ErrorDownloadSizeFailed=获取大小失敗：%1 %2
ErrorProgress=无效的進度：%1 / %2
ErrorFileSize=檔案大小錯誤：预期 %1，实際 %2

; *** TExtractionWizardPage wizard page and ExtractArchive
ExtractingLabel=正在解壓縮檔案...
ButtonStopExtraction=停止解壓縮(&S)
StopExtraction=您確定要停止解壓縮嗎？
ErrorExtractionAborted=解壓縮已中止
ErrorExtractionFailed=解壓縮失敗：%1

; *** Archive extraction failure details
ArchiveIncorrectPassword=密碼不正确
ArchiveIsCorrupted=壓縮檔已损坏
ArchiveUnsupportedFormat=不支持的壓縮檔格式

; *** "Preparing to Install" wizard page
WizardPreparing=正在準備安裝
PreparingDesc=安裝程式正在準備安裝 [name] 到您的電腦。
PreviousInstallNotCompleted=先前的程式安裝或解除安裝未完成，需要您重新啟動電腦以完成该安裝。%n%n在重新啟動電腦後，再次執行安裝程式以完成 [name] 的安裝。
CannotContinue=安裝程式不能繼續。請點擊“取消”退出。
ApplicationsFound=以下應用程式正在使用將由安裝程式更新的檔案。建議您允許安裝程式自动關閉這些應用程式。
ApplicationsFound2=以下應用程式正在使用將由安裝程式更新的檔案。建議您允許安裝程式自动關閉這些應用程式。安裝完成後，安裝程式將尝试重新启动這些應用程式。
CloseApplications=自动關閉應用程式(&A)
DontCloseApplications=不要關閉應用程式(&D)
ErrorCloseApplications=安裝程式无法自动關閉所有應用程式。建議您在繼續之前，關閉所有在使用需要由安裝程式更新的檔案的應用程式。
PrepareToInstallNeedsRestart=安裝程式必須重新啟動您的電腦。電腦重新啟動後，請再次執行安裝程式以完成 [name] 的安裝。%n%n要立即重新啟動嗎？

; *** "Installing" wizard page
WizardInstalling=正在安裝
InstallingLabel=安裝程式正在安裝 [name] 到您的電腦，請稍候。

; *** "Setup Completed" wizard page
FinishedHeadingLabel=完成 [name] 安裝精靈
FinishedLabelNoIcons=安裝程式已在您的電腦中安裝了 [name]。
FinishedLabel=安裝程式已在您的電腦中安裝了 [name]。您可以通過已安裝的捷徑執行此應用程式。
ClickFinish=點擊“完成”退出安裝程式。
FinishedRestartLabel=為完成 [name] 的安裝，安裝程式必須重新启动您的電腦。要立即重新啟動嗎？
FinishedRestartMessage=為完成 [name] 的安裝，安裝程式必須重新启动您的電腦。%n%n要立即重新啟動嗎？
ShowReadmeCheck=是，我想查閱自述檔案
YesRadio=是，立即重新啟動電腦(&Y)
NoRadio=否，稍後重新啟動電腦(&N)
; used for example as 'Run MyProg.exe'
RunEntryExec=執行 %1
; used for example as 'View Readme.txt'
RunEntryShellExec=查閱 %1

; *** "Setup Needs the Next Disk" stuff
ChangeDiskTitle=安裝程式需要下一张磁碟
SelectDiskLabel2=請插入磁碟 %1 并點擊“確定”。%n%n如果這個磁碟中的檔案可以在下列資料夾之外的資料夾中找到，請輸入正确的路径或點擊“瀏覽”。
PathLabel=路径(&P)：
FileNotInDir2=“%2”中找不到檔案“%1”。請插入正确的磁碟或選擇其他資料夾。
SelectDirectoryLabel=請指定下一张磁碟的位置。

; *** Installation phase messages
SetupAborted=安裝程式未完成安裝。%n%n請修正這個問題并重新執行安裝程式。
AbortRetryIgnoreSelectAction=選擇操作
AbortRetryIgnoreRetry=重试(&T)
AbortRetryIgnoreIgnore=忽略錯誤并繼續(&I)
AbortRetryIgnoreCancel=取消安裝
RetryCancelSelectAction=選擇操作
RetryCancelRetry=重试(&T)
RetryCancelCancel=取消

; *** Installation status messages
StatusClosingApplications=正在關閉應用程式...
StatusCreateDirs=正在建立目錄...
StatusExtractFiles=正在解壓縮檔案...
StatusDownloadFiles=正在下載檔案...
StatusCreateIcons=正在建立捷徑...
StatusCreateIniEntries=正在建立 INI 条目...
StatusCreateRegistryEntries=正在建立登錄檔条目...
StatusRegisterFiles=正在註冊檔案...
StatusSavingUninstall=正在保存解除安裝資訊...
StatusRunProgram=正在完成安裝...
StatusRestartingApplications=正在重新啟動應用程式...
StatusRollback=正在撤銷更改...

; *** Misc. errors
ErrorInternal2=内部錯誤：%1
ErrorFunctionFailedNoCode=%1 失敗
ErrorFunctionFailed=%1 失敗；錯誤代碼 %2
ErrorFunctionFailedWithMessage=%1 失敗；錯誤代碼 %2.%n%3
ErrorExecutingProgram=无法执行檔案：%n%1

; *** Registry errors
ErrorRegOpenKey=開啟登錄檔項時出錯：%n%1\%2
ErrorRegCreateKey=建立登錄檔項時出錯：%n%1\%2
ErrorRegWriteKey=写入登錄檔項時出錯：%n%1\%2

; *** INI errors
ErrorIniEntry=在檔案“%1”中建立 INI 条目時出錯。

; *** File copying errors
FileAbortRetryIgnoreSkipNotRecommended=跳過此檔案(&S)（不推荐）
FileAbortRetryIgnoreIgnoreNotRecommended=忽略錯誤并繼續(&I)（不推荐）
SourceIsCorrupted=源檔案已损坏
SourceDoesntExist=源檔案“%1”不存在
SourceVerificationFailed=源檔案驗證失敗：%1
VerificationSignatureDoesntExist=签名檔案“%1”不存在
VerificationSignatureInvalid=签名檔案“%1”无效
VerificationKeyNotFound=签名檔案“%1”使用了未知的密鑰
VerificationFileNameIncorrect=檔名不正确
VerificationFileTagIncorrect=檔案标签不正确
VerificationFileSizeIncorrect=檔案大小不正确
VerificationFileHashIncorrect=檔案哈希值不正确
ExistingFileReadOnly2=无法替换已存在的檔案，它是只讀的。
ExistingFileReadOnlyRetry=移除只讀属性并重试(&R)
ExistingFileReadOnlyKeepExisting=保留已存在的檔案(&K)
ErrorReadingExistingDest=尝试讀取已存在的檔案時出錯：
FileExistsSelectAction=選擇操作
FileExists2=檔案已经存在。
FileExistsOverwriteExisting=覆盖已存在的檔案(&O)
FileExistsKeepExisting=保留已存在的檔案(&K)
FileExistsOverwriteOrKeepAll=為接下來的冲突檔案执行此操作(&D)
ExistingFileNewerSelectAction=選擇操作
ExistingFileNewer2=已存在的檔案比安裝程式將要安裝的檔案還要新。
ExistingFileNewerOverwriteExisting=覆盖已存在的檔案(&O)
ExistingFileNewerKeepExisting=保留已存在的檔案(&K)（推荐）
ExistingFileNewerOverwriteOrKeepAll=為接下來的冲突檔案执行此操作(&D)
ErrorChangingAttr=尝试更改下列已存在的檔案属性時出錯：
ErrorCreatingTemp=尝试在目标目錄建立檔案時出錯：
ErrorReadingSource=尝试讀取下列源檔案時出錯：
ErrorCopying=尝试复制下列檔案時出錯：
ErrorDownloading=尝试下載檔案時出錯：
ErrorExtracting=尝试解壓縮壓縮檔時出錯：
ErrorReplacingExistingFile=尝试替换已存在的檔案時出錯：
ErrorRestartReplace=重新啟動并替换失敗：
ErrorRenamingTemp=尝试重命名下列目标目錄中的一個檔案時出錯：
ErrorRegisterServer=无法註冊 DLL/OCX：%1
ErrorRegSvr32Failed=RegSvr32 失敗；退出代碼 %1
ErrorRegisterTypeLib=无法註冊类型库：%1

; *** Uninstall display name markings
; used for example as 'My Program (32-bit)'
UninstallDisplayNameMark=%1 (%2)
; used for example as 'My Program (32-bit, All users)'
UninstallDisplayNameMarks=%1 (%2, %3)
UninstallDisplayNameMark32Bit=32 位
UninstallDisplayNameMark64Bit=64 位
UninstallDisplayNameMarkAllUsers=所有使用者
UninstallDisplayNameMarkCurrentUser=当前使用者

; *** Post-installation errors
ErrorOpeningReadme=尝试開啟自述檔案時出錯。
ErrorRestartingComputer=安裝程式无法重新啟動電腦，請手动重新啟動。

; *** Uninstaller messages
UninstallNotFound=檔案“%1”不存在。無法解除安裝。
UninstallOpenError=檔案“%1”不能被開啟。無法解除安裝
UninstallUnsupportedVer=此版本的解除安裝程式无法識别解除安裝紀錄檔案“%1”的格式。無法解除安裝
UninstallUnknownEntry=解除安裝紀錄中遇到一個未知条目（%1）
ConfirmUninstall=您確認要完全移除 %1 及其所有元件嗎？
UninstallOnlyOnWin64=仅允許在 64 位 Windows 中解除安裝此程式。
OnlyAdminCanUninstall=仅使用管理员权限的使用者能完成此解除安裝。
UninstallStatusLabel=正在從您的電腦中移除 %1，請稍候。
UninstalledAll=已順利從您的電腦中移除 %1。
UninstalledMost=%1 解除安裝完成。%n%n有部分内容未能被删除，但您可以手动删除它們。
UninstalledAndNeedsRestart=為完成 %1 的解除安裝，需要重新啟動您的電腦。%n%n要立即重新啟動嗎？
UninstallDataCorrupted=檔案“%1”已损坏。無法解除安裝

; *** Uninstallation phase messages
ConfirmDeleteSharedFileTitle=删除共享檔案？
ConfirmDeleteSharedFile2=系统表示下列共享檔案已不再有任何程式使用。您希望解除安裝程式删除此共享檔案嗎？%n%n如果仍有程式正在使用此檔案，删除後這些程式可能无法正常執行。如果您不能確定，請選擇“否”，保留此檔案在系统中不會造成任何损害。
SharedFileNameLabel=檔名：
SharedFileLocationLabel=位置：
WizardUninstalling=解除安裝狀態
StatusUninstalling=正在解除安裝 %1...

; *** Shutdown block reasons
ShutdownBlockReasonInstallingApp=正在安裝 %1。
ShutdownBlockReasonUninstallingApp=正在解除安裝 %1。

; The custom messages below aren't used by Setup itself, but if you make
; use of them in your scripts, you'll want to translate them.

[CustomMessages]

NameAndVersion=%1 版本 %2
AdditionalIcons=附加捷徑：
CreateDesktopIcon=建立桌面捷徑(&D)
CreateQuickLaunchIcon=建立快速启动栏捷徑(&Q)
ProgramOnTheWeb=%1 网站
UninstallProgram=解除安裝 %1
LaunchProgram=執行 %1
AssocFileExtension=將 %2 檔案扩展名與 %1 建立关联(&A)
AssocingFileExtension=正在將 %2 檔案扩展名與 %1 建立关联...
AutoStartProgramGroupDescription=启动：
AutoStartProgram=自动启动 %1
AddonHostProgramNotFound=您選擇的資料夾中无法找到 %1。%n%n您確定要繼續嗎？
