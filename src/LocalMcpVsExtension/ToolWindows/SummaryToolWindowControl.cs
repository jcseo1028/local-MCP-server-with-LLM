using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Community.VisualStudio.Toolkit;
using LocalMcpVsExtension.Services;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace LocalMcpVsExtension.ToolWindows
{
    /// <summary>
    /// Local MCP 채팅 기반 Tool Window 컨트롤.
    /// 사용자가 자연어로 메시지를 입력하면 서버가 의도를 분석하고 도구를 자동 선택·실행한다.
    /// 코드 수정 결과는 side-by-side diff로 표시되며, 사용자 확인 후 에디터에 반영된다.
    /// contracts.md §9, §10 준수.
    /// </summary>
    public sealed class SummaryToolWindowControl : UserControl
    {
        private readonly McpRestClient _client = new McpRestClient();
        private readonly BuildTestRunner _buildRunner = new BuildTestRunner();
        private readonly List<ChatMessageViewModel> _messages = new List<ChatMessageViewModel>();
        private string? _conversationId;
        private string? _currentRunId;
        private CancellationTokenSource? _pollCts;
        private bool _isBusy;

        // ── UI 요소 ──────────────────────────────────────────
        private readonly StackPanel _chatPanel;
        private readonly ScrollViewer _chatScroll;
        private readonly TextBox _txtInput;
        private readonly Button _btnSend;
        private readonly CheckBox _chkIncludeCode;
        private readonly TextBox _txtServerUrl;
        private readonly TextBlock _txtStatus;
        private readonly TextBlock _txtLanguage;
        private readonly Border _settingsBar;
        private readonly Button _btnToggleSettings;
        private readonly Button _btnNewChat;
        private readonly ComboBox _cmbHistory;
        private readonly Button _btnHistory;
        private readonly List<ChatSession> _chatSessions = new List<ChatSession>();
        private const int MaxSessionHistory = 20;

        // ── B-2: 파일 선택 UI ────────────────────────────────
        private readonly Button _btnFileSelect;
        private readonly Border _fileSelectBorder;
        private readonly StackPanel _fileSelectInner;
        /// <summary>사용자가 선택한 파일 경로 목록. 비어있으면 열린 파일 전체 사용.</summary>
        private readonly HashSet<string> _selectedFilePaths = new HashSet<string>();

        public SummaryToolWindowControl()
        {
            // ── Grid 레이아웃 (4행) ──────────────────────────────
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });       // Row 0: 설정 바
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Row 1: 채팅 영역
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });       // Row 2: 입력 영역
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });       // Row 3: 상태 표시줄

            grid.SetResourceReference(Panel.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);

            // ── Row 0: 설정 바 (접기 가능) ───────────────────────
            var settingsPanel = new DockPanel { Margin = new Thickness(6, 4, 6, 2) };

            _btnToggleSettings = CreateButton("\u2699", "설정 표시/숨기기");
            _btnToggleSettings.Width = 28;
            _btnToggleSettings.Click += BtnToggleSettings_Click;
            DockPanel.SetDock(_btnToggleSettings, Dock.Left);
            settingsPanel.Children.Add(_btnToggleSettings);

            _btnNewChat = CreateButton("\uD83D\uDCAC 새 대화", "새 대화 시작");
            _btnNewChat.Margin = new Thickness(4, 0, 0, 0);
            _btnNewChat.Click += BtnNewChat_Click;
            DockPanel.SetDock(_btnNewChat, Dock.Right);
            settingsPanel.Children.Add(_btnNewChat);

            _btnHistory = CreateButton("\uD83D\uDCCB", "이전 대화 목록");
            _btnHistory.Width = 28;
            _btnHistory.Click += BtnHistory_Click;
            DockPanel.SetDock(_btnHistory, Dock.Left);
            settingsPanel.Children.Add(_btnHistory);

            _cmbHistory = new ComboBox
            {
                Width = 180,
                Margin = new Thickness(4, 0, 0, 0),
                Visibility = Visibility.Collapsed,
                ToolTip = "이전 대화 불러오기"
            };
            _cmbHistory.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.ComboBoxStyleKey);
            _cmbHistory.SelectionChanged += CmbHistory_SelectionChanged;
            DockPanel.SetDock(_cmbHistory, Dock.Left);
            settingsPanel.Children.Add(_cmbHistory);

            _settingsBar = new Border
            {
                Margin = new Thickness(4, 0, 0, 0),
                Visibility = Visibility.Collapsed
            };

            var settingsInner = new StackPanel { Orientation = Orientation.Horizontal };
            settingsInner.Children.Add(CreateLabel("서버:"));
            _txtServerUrl = new TextBox
            {
                Text = "http://localhost:5100",
                Width = 200,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 2, 4, 2)
            };
            _txtServerUrl.SetResourceReference(Control.BackgroundProperty, VsBrushes.WindowKey);
            _txtServerUrl.SetResourceReference(Control.ForegroundProperty, VsBrushes.WindowTextKey);
            _txtServerUrl.SetResourceReference(Control.BorderBrushProperty, VsBrushes.ActiveBorderKey);
            settingsInner.Children.Add(_txtServerUrl);
            _settingsBar.Child = settingsInner;

            DockPanel.SetDock(_settingsBar, Dock.Right);
            settingsPanel.Children.Add(_settingsBar);

            // 나머지 채우기
            settingsPanel.Children.Add(new TextBlock());

            Grid.SetRow(settingsPanel, 0);
            grid.Children.Add(settingsPanel);

            // ── Row 1: 채팅 메시지 영역 ──────────────────────────
            _chatPanel = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };

            _chatScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _chatPanel,
                BorderThickness = new Thickness(0, 1, 0, 1)
            };
            _chatScroll.SetResourceReference(Control.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
            _chatScroll.SetResourceReference(Control.BorderBrushProperty, VsBrushes.ActiveBorderKey);

            Grid.SetRow(_chatScroll, 1);
            grid.Children.Add(_chatScroll);

            // 환영 메시지
            AddSystemMessage("안녕하세요! 코드에 대한 질문이나 작업을 요청해 주세요.\n\n" +
                "사전 조건:\n" +
                "  1. Ollama 실행 중 (ollama serve)\n" +
                "  2. MCP 서버 실행 중 (dotnet run)");

            // ── Row 2: 입력 영역 ─────────────────────────────────
            var inputPanel = new DockPanel { Margin = new Thickness(6, 2, 6, 4) };

            _btnSend = CreateButton("전송 \u27A4");
            _btnSend.Padding = new Thickness(12, 6, 12, 6);
            _btnSend.Click += BtnSend_Click;
            DockPanel.SetDock(_btnSend, Dock.Right);
            inputPanel.Children.Add(_btnSend);

            var inputInner = new StackPanel();

            _txtInput = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 36,
                MaxHeight = 100,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(6, 4, 6, 4)
            };
            _txtInput.SetResourceReference(Control.BackgroundProperty, VsBrushes.WindowKey);
            _txtInput.SetResourceReference(Control.ForegroundProperty, VsBrushes.WindowTextKey);
            _txtInput.SetResourceReference(Control.BorderBrushProperty, VsBrushes.ActiveBorderKey);
            _txtInput.PreviewKeyDown += TxtInput_PreviewKeyDown;
            inputInner.Children.Add(_txtInput);

            _chkIncludeCode = new CheckBox
            {
                Content = "현재 코드 포함",
                IsChecked = true,
                Margin = new Thickness(0, 4, 0, 0)
            };
            _chkIncludeCode.SetResourceReference(Control.ForegroundProperty, VsBrushes.ToolWindowTextKey);

            // B-2: 파일 선택 버튼 행
            var fileRow = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            _btnFileSelect = new Button
            {
                Content = "\uD83D\uDCC1 파일 선택",
                Padding = new Thickness(6, 2, 6, 2),
                FontSize = 11
            };
            _btnFileSelect.SetResourceReference(Control.BackgroundProperty, VsBrushes.ButtonFaceKey);
            _btnFileSelect.SetResourceReference(Control.ForegroundProperty, VsBrushes.ButtonTextKey);
            _btnFileSelect.SetResourceReference(Control.BorderBrushProperty, VsBrushes.ActiveBorderKey);
            _btnFileSelect.Click += BtnFileSelect_Click;
            DockPanel.SetDock(_btnFileSelect, Dock.Left);
            fileRow.Children.Add(_btnFileSelect);
            var _lblFileSelectHint = new TextBlock
            {
                Text = "  (모든 열린 파일 포함)",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            _lblFileSelectHint.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.GrayTextKey);
            _lblFileSelectHint.Name = "FileSelectHint";
            fileRow.Children.Add(_lblFileSelectHint);

            // B-2: 파일 체크리스트 패널 (토글)
            _fileSelectInner = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            _fileSelectBorder = new Border
            {
                Child = new ScrollViewer
                {
                    Content = _fileSelectInner,
                    MaxHeight = 150,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                },
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4),
                Margin = new Thickness(0, 2, 0, 2),
                Visibility = Visibility.Collapsed
            };
            _fileSelectBorder.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ActiveBorderKey);
            _fileSelectBorder.SetResourceReference(Border.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);

            inputInner.Children.Add(_chkIncludeCode);
            inputInner.Children.Add(fileRow);
            inputInner.Children.Add(_fileSelectBorder);

            inputPanel.Children.Add(inputInner);

            Grid.SetRow(inputPanel, 2);
            grid.Children.Add(inputPanel);

            // ── Row 3: 상태 표시줄 ───────────────────────────────
            var statusBar = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(6, 4, 6, 4)
            };
            statusBar.SetResourceReference(Border.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
            statusBar.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ActiveBorderKey);

            var statusDock = new DockPanel();

            _txtStatus = new TextBlock { Text = "준비" };
            _txtStatus.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            DockPanel.SetDock(_txtStatus, Dock.Left);
            statusDock.Children.Add(_txtStatus);

            _txtLanguage = new TextBlock
            {
                Text = "",
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _txtLanguage.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            DockPanel.SetDock(_txtLanguage, Dock.Right);
            statusDock.Children.Add(_txtLanguage);

            statusBar.Child = statusDock;
            Grid.SetRow(statusBar, 3);
            grid.Children.Add(statusBar);

            Content = grid;
        }

        // ── 테마 적용 헬퍼 ───────────────────────────────────────

        private TextBlock CreateLabel(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            return tb;
        }

        private Button CreateButton(string content, string? tooltip = null)
        {
            var btn = new Button
            {
                Content = content,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = tooltip
            };
            btn.SetResourceReference(Control.BackgroundProperty, VsBrushes.ButtonFaceKey);
            btn.SetResourceReference(Control.ForegroundProperty, VsBrushes.ButtonTextKey);
            btn.SetResourceReference(Control.BorderBrushProperty, VsBrushes.ActiveBorderKey);
            return btn;
        }

        // ── 이벤트 핸들러 ────────────────────────────────────────

#pragma warning disable VSSDK007

        private void BtnToggleSettings_Click(object sender, RoutedEventArgs e)
        {
            _settingsBar.Visibility = _settingsBar.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void BtnHistory_Click(object sender, RoutedEventArgs e)
        {
            _cmbHistory.Visibility = _cmbHistory.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void BtnNewChat_Click(object sender, RoutedEventArgs e)
        {
            BackupCurrentSession();

            _conversationId = null!;
            _messages.Clear();
            _chatPanel.Children.Clear();
            AddSystemMessage("새로운 대화를 시작합니다.");
            _txtStatus.Text = "준비";
        }

        private void CmbHistory_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_cmbHistory.SelectedItem is ChatSession session)
            {
                // 현재 대화를 백업한 뒤 선택된 세션을 복원
                BackupCurrentSession();
                RestoreSession(session);
                _cmbHistory.SelectedIndex = -1;
                _cmbHistory.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await SendMessageAsync();
            });
        }

#pragma warning disable VSSDK007
        private void BtnFileSelect_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await PopulateFileSelectPanelAsync();
            });
        }
#pragma warning restore VSSDK007

        private async Task PopulateFileSelectPanelAsync()
        {
            // 패널이 이미 열려있으면 닫기
            if (_fileSelectBorder.Visibility == Visibility.Visible)
            {
                _fileSelectBorder.Visibility = Visibility.Collapsed;
                return;
            }

            // 열린 코드 파일 전체 수집
            var allFiles = await CollectOpenFilesAsync();
            if (allFiles.Length == 0)
            {
                _txtStatus.Text = "열린 코드 파일이 없습니다.";
                return;
            }

            _fileSelectInner.Children.Clear();

            var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var lblHeader = new TextBlock { Text = "포함할 파일을 선택하세요:", FontWeight = FontWeights.Bold, FontSize = 11 };
            lblHeader.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            DockPanel.SetDock(lblHeader, Dock.Left);
            headerRow.Children.Add(lblHeader);

            var btnAll = new Button { Content = "전체", FontSize = 10, Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(8, 0, 0, 0) };
            btnAll.SetResourceReference(Control.BackgroundProperty, VsBrushes.ButtonFaceKey);
            btnAll.SetResourceReference(Control.ForegroundProperty, VsBrushes.ButtonTextKey);
            DockPanel.SetDock(btnAll, Dock.Right);
            headerRow.Children.Add(btnAll);

            var btnNone = new Button { Content = "없음", FontSize = 10, Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(4, 0, 0, 0) };
            btnNone.SetResourceReference(Control.BackgroundProperty, VsBrushes.ButtonFaceKey);
            btnNone.SetResourceReference(Control.ForegroundProperty, VsBrushes.ButtonTextKey);
            DockPanel.SetDock(btnNone, Dock.Right);
            headerRow.Children.Add(btnNone);

            _fileSelectInner.Children.Add(headerRow);

            var checkboxes = new List<(CheckBox cb, string path)>();

            foreach (var f in allFiles)
            {
                var isChecked = _selectedFilePaths.Count == 0 || _selectedFilePaths.Contains(f.FilePath);
                var cb = new CheckBox
                {
                    Content = System.IO.Path.GetFileName(f.FilePath),
                    ToolTip = f.FilePath,
                    IsChecked = isChecked,
                    Margin = new Thickness(0, 1, 0, 1),
                    FontSize = 11
                };
                cb.SetResourceReference(Control.ForegroundProperty, VsBrushes.ToolWindowTextKey);
                cb.Checked += (s, ev) => { _selectedFilePaths.Add(f.FilePath); UpdateFileSelectHint(); };
                cb.Unchecked += (s, ev) => { _selectedFilePaths.Remove(f.FilePath); UpdateFileSelectHint(); };
                if (isChecked) _selectedFilePaths.Add(f.FilePath);
                checkboxes.Add((cb, f.FilePath));
                _fileSelectInner.Children.Add(cb);
            }

            btnAll.Click += (s, e) =>
            {
                foreach (var (cb, path) in checkboxes) { cb.IsChecked = true; _selectedFilePaths.Add(path); }
                UpdateFileSelectHint();
            };
            btnNone.Click += (s, e) =>
            {
                foreach (var (cb, _) in checkboxes) cb.IsChecked = false;
                _selectedFilePaths.Clear();
                UpdateFileSelectHint();
            };

            _fileSelectBorder.Visibility = Visibility.Visible;
            UpdateFileSelectHint();
        }

        private void UpdateFileSelectHint()
        {
            // _fileSelectBorder의 부모(inputInner) 에서 힌트 텍스트블록 찾기
            var inputInner = _fileSelectBorder.Parent as StackPanel;
            if (inputInner == null) return;
            var hint = inputInner.Children.OfType<DockPanel>()
                .SelectMany(dp => dp.Children.OfType<TextBlock>())
                .FirstOrDefault(tb => tb.Name == "FileSelectHint");
            if (hint == null) return;

            if (_selectedFilePaths.Count == 0)
                hint.Text = "  (모든 열린 파일 포함)";
            else
                hint.Text = $"  ({_selectedFilePaths.Count}개 선택됨)";
        }

        private void TxtInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                e.Handled = true;
#pragma warning disable VSSDK007
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await SendMessageAsync();
                });
#pragma warning restore VSSDK007
            }
        }
#pragma warning restore VSSDK007

        // ── 메시지 전송 ──────────────────────────────────────────

        private async Task SendMessageAsync()
        {
            var message = _txtInput.Text?.Trim();
            if (string.IsNullOrEmpty(message) || _isBusy) return;

            _txtInput.Text = "";
            SetBusy(true);

            AddUserMessage(message!);

            try
            {
                string? code = null;
                string? language = null;
                bool selectionOnly = false;
                string? activeFilePath = null;

                if (_chkIncludeCode.IsChecked == true)
                {
                    var docView = await VS.Documents.GetActiveDocumentViewAsync();
                    if (docView?.TextBuffer != null)
                    {
                        activeFilePath = docView.FilePath;
                        var selection = docView.TextView?.Selection;
                        if (selection != null && selection.SelectedSpans.Count > 0)
                        {
                            var selectedText = string.Join(Environment.NewLine,
                                selection.SelectedSpans.Select(span => span.GetText()));
                            if (!string.IsNullOrWhiteSpace(selectedText))
                            {
                                code = selectedText;
                                selectionOnly = true;
                            }
                        }

                        if (code == null)
                        {
                            var fullCode = docView.TextBuffer.CurrentSnapshot.GetText();
                            if (!string.IsNullOrWhiteSpace(fullCode))
                                code = fullCode;
                        }

                        language = LanguageDetector.FromFilePath(docView.FilePath);
                        _txtLanguage.Text = "언어: " + language;
                    }
                }

                var serverUrl = _txtServerUrl.Text.TrimEnd('/');
                _txtStatus.Text = "Run 시작 중...";

                // Run 시작
                var solutionPath = await GetSolutionPathAsync();
                var openFiles = await CollectOpenFilesAsync();
                var startReq = new RunStartRequest
                {
                    Message = message!,
                    Code = code,
                    Language = language,
                    SelectionOnly = selectionOnly,
                    ConversationId = _conversationId ?? "",
                    ActiveFilePath = activeFilePath,
                    SolutionPath = solutionPath,
                    Files = openFiles.Length > 0 ? openFiles : null
                };

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var startResp = await _client.StartRunAsync(serverUrl, startReq);
                _conversationId = startResp.ConversationId;
                _currentRunId = startResp.RunId;

                // Run 진행 상태 표시용 메시지
                var runVm = new ChatMessageViewModel
                {
                    Role = ChatMessageRole.Assistant,
                    Content = "처리 중...",
                    RunId = startResp.RunId,
                    RunViewModel = new ChatRunViewModel { RunId = startResp.RunId }
                };
                _messages.Add(runVm);
                RenderMessage(runVm);

                // 폴링으로 상태 추적
                _pollCts?.Cancel();
                _pollCts = new CancellationTokenSource();
                await PollRunAsync(serverUrl, startResp.RunId, runVm, selectionOnly, sw, _pollCts.Token);
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                AddAssistantMessage(
                    "MCP 서버에 연결할 수 없습니다.\n\n" +
                    "서버가 실행 중인지 확인하세요.\n" +
                    "  cd src/LocalMcpServer && dotnet run\n\n" +
                    "오류: " + ex.Message);
                _txtStatus.Text = "연결 실패";
                SetBusy(false);
            }
            catch (TaskCanceledException)
            {
                AddAssistantMessage("요청 시간이 초과되었습니다.\nOllama가 실행 중인지 확인하세요.");
                _txtStatus.Text = "시간 초과";
                SetBusy(false);
            }
            catch (Exception ex)
            {
                AddAssistantMessage("오류가 발생했습니다:\n" + ex.Message);
                _txtStatus.Text = "오류";
                SetBusy(false);
            }
        }

        private async Task PollRunAsync(string serverUrl, string runId,
            ChatMessageViewModel runVm, bool selectionOnly,
            System.Diagnostics.Stopwatch sw, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(750, ct);

                    var snapshot = await _client.GetRunAsync(serverUrl, runId);
                    if (runVm.RunViewModel != null)
                        runVm.RunViewModel.UpdateFrom(snapshot);

                    // 단계 진행 상태를 표시
                    UpdateRunStageDisplay(runVm, snapshot);

                    if (snapshot.State == "WaitingForApproval")
                    {
                        _txtStatus.Text = string.Format("승인 대기 중 ({0:F1}초)", sw.Elapsed.TotalSeconds);

                        if (snapshot.Proposal != null && snapshot.Proposal.RequiresApproval)
                        {
                            runVm.HasCodeChange = true;
                            runVm.CodeChange = new CodeChangeInfo
                            {
                                Original = snapshot.Proposal.Original ?? "",
                                Modified = snapshot.Proposal.Modified ?? "",
                                ToolName = snapshot.Intent?.ToolName ?? "",
                                SelectionOnly = selectionOnly
                            };

                            // v2.2: 멀티 파일 변경이 있으면 Files 목록 설정
                            if (snapshot.Proposal.IsMultiFile && snapshot.Proposal.Changes != null)
                            {
                                var fileList = new System.Collections.Generic.List<FileChangeInfo>();
                                foreach (var fc in snapshot.Proposal.Changes)
                                {
                                    // A-2: 서버 사전 계산 hunks가 있으면 HunkSelections 미리 초기화
                                    List<HunkSelection> preHunks = null;
                                    if (fc.Hunks != null && fc.Hunks.Length > 0)
                                    {
                                        preHunks = new List<HunkSelection>(fc.Hunks.Length);
                                        for (int pi = 0; pi < fc.Hunks.Length; pi++)
                                        {
                                            var h = fc.Hunks[pi];
                                            preHunks.Add(new HunkSelection
                                            {
                                                HunkIndex  = pi,
                                                IsAccepted = true,
                                                Hunk       = new DiffHunk(h.OriginalStart, h.OriginalEnd, h.NewText)
                                            });
                                        }
                                    }
                                    fileList.Add(new FileChangeInfo
                                    {
                                        FilePath      = fc.FilePath,
                                        Original      = fc.Original,
                                        Modified      = fc.Modified,
                                        SelectionOnly = fc.SelectionOnly,
                                        IsNewFile     = fc.IsNewFile,
                                        Description   = fc.Description,
                                        HunkSelections = preHunks
                                    });
                                }
                                runVm.CodeChange.Files = fileList;
                            }

                            runVm.Approval = ApprovalState.Pending;

                            // UI 업데이트: diff + 승인 버튼 추가
                            RenderRunApproval(runVm, snapshot);
                        }

                        SetBusy(false);
                        return; // 폴링 중단, 사용자 승인 대기
                    }

                    if (snapshot.State == "Completed" || snapshot.State == "Rejected" || snapshot.State == "Failed")
                    {
                        sw.Stop();

                        // 최종 결과 표시
                        string content;
                        if (!string.IsNullOrEmpty(snapshot.Error))
                        {
                            content = "오류: " + snapshot.Error;
                        }
                        else if (snapshot.Proposal != null && !snapshot.Proposal.RequiresApproval
                                 && !string.IsNullOrEmpty(snapshot.Proposal.Summary))
                        {
                            // 승인 불필요(요약/채팅)인 경우: Proposal.Summary가 핵심 결과
                            content = snapshot.Proposal.Summary;
                        }
                        else
                        {
                            content = snapshot.FinalSummary ?? snapshot.Proposal?.Summary ?? "(결과 없음)";
                        }

                        UpdateRunContent(runVm, content, snapshot);
                        _txtStatus.Text = string.Format("{0} ({1:F1}초)", snapshot.State, sw.Elapsed.TotalSeconds);
                        SetBusy(false);
                        return;
                    }

                    // Running 상태 업데이트
                    var activeStage = snapshot.Stages?.FirstOrDefault(
                        s => s.Status == "InProgress");
                    _txtStatus.Text = activeStage != null
                        ? string.Format("{0}... ({1:F1}초)", activeStage.Title, sw.Elapsed.TotalSeconds)
                        : string.Format("처리 중... ({0:F1}초)", sw.Elapsed.TotalSeconds);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                AddAssistantMessage("폴링 오류: " + ex.Message);
                _txtStatus.Text = "오류";
                SetBusy(false);
            }
        }

        private void UpdateRunStageDisplay(ChatMessageViewModel runVm, RunSnapshot snapshot)
        {
            if (!(runVm.Tag is Border border && border.Child is StackPanel panel))
                return;

            var (fg, bg) = GetThemeColors();
            bool isDark = IsDark(bg);

            // ── 단계 타임라인 (§9b 색상 구분) ──
            var timelinePanel = panel.Children.OfType<StackPanel>()
                .FirstOrDefault(p => p.Name == "StageTimeline");

            if (timelinePanel == null)
            {
                timelinePanel = new StackPanel
                {
                    Name = "StageTimeline",
                    Margin = new Thickness(0, 4, 0, 4)
                };
                // 맨 앞에 삽입
                if (panel.Children.Count > 0)
                    panel.Children.Insert(0, timelinePanel);
                else
                    panel.Children.Add(timelinePanel);
            }

            timelinePanel.Children.Clear();

            if (snapshot.Stages != null)
            {
                foreach (var stage in snapshot.Stages)
                {
                    var stagePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };

                    string icon;
                    Color iconColor;
                    switch (stage.Status)
                    {
                        case "Completed":
                            icon = "\u2705";
                            iconColor = isDark ? Color.FromRgb(78, 201, 176) : Color.FromRgb(22, 163, 74);
                            break;
                        case "InProgress":
                            icon = "\u23F3";
                            iconColor = isDark ? Color.FromRgb(86, 156, 214) : Color.FromRgb(37, 99, 235);
                            break;
                        case "Skipped":
                            icon = "\u23ED";
                            iconColor = isDark ? Color.FromRgb(128, 128, 128) : Color.FromRgb(156, 163, 175);
                            break;
                        case "Failed":
                            icon = "\u274C";
                            iconColor = isDark ? Color.FromRgb(244, 63, 94) : Color.FromRgb(220, 38, 38);
                            break;
                        default: // Pending
                            icon = "\u25CB";
                            iconColor = isDark ? Color.FromRgb(100, 100, 100) : Color.FromRgb(180, 180, 180);
                            break;
                    }

                    var iconBlock = new TextBlock
                    {
                        Text = icon,
                        FontSize = 11,
                        Width = 18,
                        Foreground = new SolidColorBrush(iconColor)
                    };
                    stagePanel.Children.Add(iconBlock);

                    var titleBlock = new TextBlock
                    {
                        Text = stage.Title,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(
                            stage.Status == "Pending" ? iconColor : fg)
                    };
                    if (stage.Status == "Skipped")
                        titleBlock.TextDecorations = TextDecorations.Strikethrough;
                    stagePanel.Children.Add(titleBlock);

                    if (!string.IsNullOrEmpty(stage.Message))
                    {
                        var msgBlock = new TextBlock
                        {
                            Text = " — " + stage.Message,
                            FontSize = 10,
                            Foreground = new SolidColorBrush(isDark
                                ? Color.FromRgb(160, 160, 160)
                                : Color.FromRgb(120, 120, 120)),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            MaxWidth = 300
                        };
                        stagePanel.Children.Add(msgBlock);
                    }

                    timelinePanel.Children.Add(stagePanel);
                }
            }

            // ── 계획 섹션 (§9a) ──
            var runVmData = runVm.RunViewModel;
            if (runVmData != null && runVmData.PlanItems.Count > 0)
            {
                var planPanel = panel.Children.OfType<Border>()
                    .FirstOrDefault(b => b.Name == "PlanSection");
                if (planPanel == null)
                {
                    planPanel = CreateRunSection("PlanSection", "\uD83D\uDCCB 계획", isDark);
                    InsertAfter(panel, timelinePanel, planPanel);
                }
                var planContent = planPanel.Child as StackPanel;
                if (planContent != null && planContent.Children.Count <= 1) // title only
                {
                    foreach (var item in runVmData.PlanItems)
                    {
                        planContent.Children.Add(new TextBlock
                        {
                            Text = "• " + item,
                            FontSize = 11,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = new SolidColorBrush(fg),
                            Margin = new Thickness(8, 1, 0, 1)
                        });
                    }
                }
            }

            // ── 참조 섹션 (§9a) ──
            if (runVmData != null && runVmData.References.Count > 0)
            {
                var refPanel = panel.Children.OfType<Border>()
                    .FirstOrDefault(b => b.Name == "RefSection");
                if (refPanel == null)
                {
                    refPanel = CreateRunSection("RefSection", "\uD83D\uDD0D 참조 문서", isDark);
                    var planSect = panel.Children.OfType<Border>()
                        .FirstOrDefault(b => b.Name == "PlanSection");
                    InsertAfter(panel, planSect ?? (UIElement)timelinePanel, refPanel);
                }
                var refContent = refPanel.Child as StackPanel;
                if (refContent != null && refContent.Children.Count <= 1)
                {
                    foreach (var r in runVmData.References)
                    {
                        var rBlock = new TextBlock
                        {
                            Text = $"• {r.Title} ({r.Source})",
                            FontSize = 10,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = new SolidColorBrush(fg),
                            Margin = new Thickness(8, 1, 0, 1),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            MaxHeight = 30
                        };
                        rBlock.ToolTip = r.Excerpt;
                        refContent.Children.Add(rBlock);
                    }
                }
            }

            _chatScroll.ScrollToEnd();
        }

        private Border CreateRunSection(string name, string title, bool isDark)
        {
            var border = new Border
            {
                Name = name,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(isDark
                    ? Color.FromRgb(38, 38, 42)
                    : Color.FromRgb(248, 248, 248)),
                BorderBrush = new SolidColorBrush(isDark
                    ? Color.FromRgb(55, 55, 60)
                    : Color.FromRgb(220, 220, 220)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 4, 0, 2)
            };

            var panel = new StackPanel();
            var titleBlock = new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 2)
            };
            titleBlock.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            panel.Children.Add(titleBlock);

            border.Child = panel;
            return border;
        }

        private static void InsertAfter(StackPanel parent, UIElement after, UIElement toInsert)
        {
            int idx = parent.Children.IndexOf(after);
            if (idx >= 0 && idx < parent.Children.Count - 1)
                parent.Children.Insert(idx + 1, toInsert);
            else
                parent.Children.Add(toInsert);
        }

        private void UpdateRunContent(ChatMessageViewModel runVm, string content, RunSnapshot snapshot)
        {
            runVm.Content = content;

            if (runVm.Tag is Border border)
            {
                var (fg, bg) = GetThemeColors();
                var panel = border.Child as StackPanel ?? new StackPanel();

                // 기존 FlowDocumentScrollViewer를 제거하고 새로 생성
                var toRemove = panel.Children.OfType<FlowDocumentScrollViewer>().ToList();
                foreach (var item in toRemove)
                    panel.Children.Remove(item);

                try
                {
                    var flowDoc = MarkdownToFlowDocument.Convert(content, fg, bg);
                    var docViewer = new FlowDocumentScrollViewer
                    {
                        Document = flowDoc,
                        IsToolBarVisible = false,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0)
                    };
                    panel.Children.Add(docViewer);
                }
                catch
                {
                    var plainText = new TextBlock
                    {
                        Text = content,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(fg)
                    };
                    panel.Children.Add(plainText);
                }
            }
        }

        private void RenderRunApproval(ChatMessageViewModel runVm, RunSnapshot snapshot)
        {
            if (runVm.Tag is Border border && border.Child is StackPanel panel)
            {
                var (fg, bg) = GetThemeColors();
                bool isDark = IsDark(bg);

                // Diff 뷰 추가
                panel.Children.Add(CreateDiffView(runVm, fg, bg, isDark));
                // 승인 버튼 추가 (Run API 용)
                panel.Children.Add(CreateRunApprovalButtons(runVm));
            }
        }

        private UIElement CreateRunApprovalButtons(ChatMessageViewModel msg)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 0)
            };

            var btnApprove = new Button
            {
                Content = "\u2705 확인",
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 8, 0)
            };
            btnApprove.SetResourceReference(Control.BackgroundProperty, VsBrushes.ButtonFaceKey);
            btnApprove.SetResourceReference(Control.ForegroundProperty, VsBrushes.ButtonTextKey);
            btnApprove.SetResourceReference(Control.BorderBrushProperty, VsBrushes.ActiveBorderKey);
            btnApprove.Click += (s, e) =>
            {
#pragma warning disable VSSDK007
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ApproveRunChangeAsync(msg);
                });
#pragma warning restore VSSDK007
            };
            panel.Children.Add(btnApprove);

            var btnReject = new Button
            {
                Content = "\u274C 거부",
                Padding = new Thickness(10, 4, 10, 4)
            };
            btnReject.SetResourceReference(Control.BackgroundProperty, VsBrushes.ButtonFaceKey);
            btnReject.SetResourceReference(Control.ForegroundProperty, VsBrushes.ButtonTextKey);
            btnReject.SetResourceReference(Control.BorderBrushProperty, VsBrushes.ActiveBorderKey);
            btnReject.Click += (s, e) =>
            {
#pragma warning disable VSSDK007
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await RejectRunChangeAsync(msg);
                });
#pragma warning restore VSSDK007
            };
            panel.Children.Add(btnReject);

            msg.ApprovalPanel = panel;
            return panel;
        }

        private async Task ApproveRunChangeAsync(ChatMessageViewModel msg)
        {
            if (msg.CodeChange == null || msg.RunId == null) return;

            try
            {
                // 에디터에 코드 적용
                var docView = await VS.Documents.GetActiveDocumentViewAsync();
                if (docView?.TextBuffer == null)
                {
                    _txtStatus.Text = "열린 문서가 없습니다.";
                    return;
                }

                var modifiedCode = msg.CodeChange.Modified;
                var snapshot = docView.TextBuffer.CurrentSnapshot;
                modifiedCode = NormalizeNewlines(modifiedCode, snapshot);
                modifiedCode = NormalizeIndentation(modifiedCode, snapshot);

                // 적용 전 기본 검증: 잘린 코드 보호
                var originalCode = msg.CodeChange.SelectionOnly
                    ? msg.CodeChange.Original
                    : snapshot.GetText();
                if (!string.IsNullOrEmpty(originalCode) && !string.IsNullOrEmpty(modifiedCode))
                {
                    // 수정 코드가 원본의 30% 미만이면 LLM 출력이 잘렸을 가능성이 높음
                    var ratio = (double)modifiedCode.Length / originalCode.Length;
                    if (ratio < 0.3)
                    {
                        _txtStatus.Text = $"적용 중단: 수정 코드가 원본 대비 너무 짧습니다 ({ratio:P0}). LLM 출력이 잘렸을 수 있습니다.";
                        msg.Approval = ApprovalState.Rejected;
                        UpdateApprovalUI(msg);
                        var serverUrl2 = _txtServerUrl.Text.TrimEnd('/');
                        await _client.SendRunApprovalAsync(serverUrl2, msg.RunId, false);
                        return;
                    }
                }

                // §8g: 적용 실패 분기 — 원본 매칭 실패 시 build_test Skipped
                var applyFailed = false;
                string? applyErrorMsg = null;

                try
                {
                    if (msg.CodeChange.IsMultiFile && msg.CodeChange.Files != null)
                    {
                        // v2.2 / B-1 / B-3 / C-2: 파일별 선택 적용 + Atomic Rollback + ApplyResults 수집
                        var perFileResults = new System.Collections.Generic.List<FileApplyResultDto>();
                        // B-3: 롤백용 원본 내용 백업 (적용 성공한 파일들만 저장)
                        var appliedBackups = new System.Collections.Generic.Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase);
                        var failedFiles = new System.Collections.Generic.List<string>();
                        bool breakProcessing = false;

                        foreach (var fc in msg.CodeChange.Files)
                        {
                            if (breakProcessing) break;

                            // B-1: 사용자가 체크 해제한 파일은 건너뜀
                            if (!fc.IsSelected)
                            {
                                perFileResults.Add(new FileApplyResultDto
                                {
                                    FilePath = fc.FilePath,
                                    Applied = false,
                                    Message = "사용자가 제외"
                                });
                                continue;
                            }

                            try
                            {
                                // B-3: 적용 전 원본 백업
                                if (!fc.IsNewFile && System.IO.File.Exists(fc.FilePath))
                                    appliedBackups[fc.FilePath] = System.IO.File.ReadAllText(fc.FilePath);

                                if (fc.IsNewFile)
                                    await CreateNewFileAsync(fc.FilePath, fc.Modified);
                                else
                                    await ApplyHunksToFileAsync(fc);

                                perFileResults.Add(new FileApplyResultDto
                                {
                                    FilePath = fc.FilePath,
                                    Applied = true
                                });
                            }
                            catch (Exception fcEx)
                            {
                                // B-3: 실패 시 지금까지 적용된 파일 모두 원복 (atomic rollback)
                                foreach (var kvp in appliedBackups)
                                {
                                    try { System.IO.File.WriteAllText(kvp.Key, kvp.Value, System.Text.Encoding.UTF8); }
                                    catch { /* 원복 실패는 무시 */ }
                                }

                                perFileResults.Add(new FileApplyResultDto
                                {
                                    FilePath = fc.FilePath,
                                    Applied = false,
                                    Message = fcEx.Message
                                });
                                failedFiles.Add($"{fc.FilePath}: {fcEx.Message}");
                                breakProcessing = true;
                            }
                        }

                        // C-2: ApplyResults 보존
                        msg.CodeChange.ApplyResults = perFileResults;

                        if (failedFiles.Count > 0)
                        {
                            applyFailed = true;
                            applyErrorMsg = $"적용 실패 — 롤백됨 ({appliedBackups.Count}개 파일 원복): "
                                + string.Join("; ", failedFiles);
                        }
                    }
                    else
                    {
                        // 단일 파일 경로 (기존 로직)
                        var originalText = msg.CodeChange.SelectionOnly
                            ? NormalizeNewlines(msg.CodeChange.Original ?? string.Empty, snapshot)
                            : snapshot.GetText();

                        // SelectionOnly: 문서 내 원본 위치(offset) 보정
                        int baseOffset = 0;
                        if (msg.CodeChange.SelectionOnly && !string.IsNullOrEmpty(msg.CodeChange.Original))
                        {
                            var fullText = snapshot.GetText();
                            baseOffset = fullText.IndexOf(originalText, StringComparison.Ordinal);
                            if (baseOffset < 0)
                            {
                                applyFailed = true;
                                applyErrorMsg = "원본 텍스트 매칭 실패: 문서가 변경되었을 수 있습니다.";
                                goto ApplyFailed;
                            }
                        }

                        // diff 계산 (라인 단위)
                        var allHunks = LineDiffEngine.Compute(originalText, modifiedCode).ToList();

                        // A-1: HunkSelections이 있으면 선택된 hunk만 적용
                        IEnumerable<DiffHunk> hunksToApply;
                        var hunkSels = msg.CodeChange?.HunkSelections;
                        if (hunkSels != null && hunkSels.Count == allHunks.Count)
                            hunksToApply = allHunks.Where((h, i) => hunkSels[i].IsAccepted);
                        else
                            hunksToApply = allHunks;

                        var filteredHunks = hunksToApply.ToList();
                        if (filteredHunks.Count == 0)
                        {
                            _txtStatus.Text = "변경 없음: LLM 출력이 원본과 동일하거나 모든 hunk가 거부됐습니다.";
                        }
                        else
                        {
                            // 라인 배열 레벨에서 재구성 후 전체 교체
                            var origLines = SplitToLinesList(originalText);
                            foreach (var hunk in System.Linq.Enumerable.OrderByDescending(
                                filteredHunks, h => h.OriginalStart))
                            {
                                var newLines = SplitToLinesList(hunk.NewText);
                                int removeCount = Math.Min(
                                    hunk.OriginalEnd - hunk.OriginalStart,
                                    origLines.Count - hunk.OriginalStart);
                                if (removeCount > 0)
                                    origLines.RemoveRange(hunk.OriginalStart, removeCount);
                                origLines.InsertRange(hunk.OriginalStart, newLines);
                            }
                            var resultText = string.Concat(origLines);

                            using (var edit = docView.TextBuffer.CreateEdit())
                            {
                                if (msg.CodeChange.SelectionOnly && baseOffset >= 0)
                                    edit.Replace(new Microsoft.VisualStudio.Text.Span(baseOffset, originalText.Length), resultText);
                                else
                                    edit.Replace(new Microsoft.VisualStudio.Text.Span(0, snapshot.Length), resultText);
                                edit.Apply();
                            }
                        }
                    } // end else (single-file)
                }
                catch (Exception applyEx)
                {
                    applyFailed = true;
                    applyErrorMsg = "코드 적용 중 오류: " + applyEx.Message;
                }

                ApplyFailed:

                msg.Approval = ApprovalState.Approved;
                UpdateApprovalUI(msg);

                // 서버에 승인 알림
                var serverUrl = _txtServerUrl.Text.TrimEnd('/');
                await _client.SendRunApprovalAsync(serverUrl, msg.RunId, true);

                ClientResultRequest clientResult;

                if (applyFailed)
                {
                    // §8g: 적용 실패 시 build_test는 Skipped 처리
                    _txtStatus.Text = "적용 실패: " + applyErrorMsg;
                    clientResult = new ClientResultRequest
                    {
                        Applied = false,
                        ApplyMessage = applyErrorMsg,
                        ApplyResults = msg.CodeChange?.ApplyResults?.ToArray(),
                        Build = new ClientBuildResult { Attempted = false, Summary = "적용 실패로 빌드 생략" },
                        Tests = new ClientTestResult { Attempted = false, Summary = "적용 실패로 테스트 생략" }
                    };
                }
                else
                {
                    _txtStatus.Text = "적용 완료. 빌드/테스트 실행 중...";
                    SetBusy(true);

                    // 빌드/테스트 실행
                    clientResult = new ClientResultRequest
                    {
                        Applied = true,
                        ApplyResults = msg.CodeChange?.ApplyResults?.ToArray()
                    };
                    var solutionPath = await GetSolutionPathAsync();
                    if (!string.IsNullOrEmpty(solutionPath))
                    {
                        var buildResult = await _buildRunner.BuildAsync(solutionPath!);
                        clientResult.Build = new ClientBuildResult
                        {
                            Attempted = buildResult.Attempted,
                            Succeeded = buildResult.Succeeded,
                            Summary = buildResult.Summary
                        };

                        if (buildResult.Succeeded == true)
                        {
                            var testResult = await _buildRunner.TestAsync(solutionPath!);
                            clientResult.Tests = new ClientTestResult
                            {
                                Attempted = testResult.Attempted,
                                Succeeded = testResult.Succeeded,
                                Summary = testResult.Summary
                            };
                        }
                    }
                }

                // 결과를 서버로 전송
                var finalSnapshot = await _client.SendClientResultAsync(serverUrl, msg.RunId, clientResult);

                // 폴링 재개하여 최종 요약 대기
                _pollCts?.Cancel();
                _pollCts = new CancellationTokenSource();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await PollRunAsync(serverUrl, msg.RunId, msg, msg.CodeChange?.SelectionOnly ?? false, sw, _pollCts.Token);
            }
            catch (Exception ex)
            {
                _txtStatus.Text = "적용 실패: " + ex.Message;
                SetBusy(false);
            }
        }

        private async Task RejectRunChangeAsync(ChatMessageViewModel msg)
        {
            if (msg.RunId == null) return;

            msg.Approval = ApprovalState.Rejected;
            UpdateApprovalUI(msg);

            try
            {
                var serverUrl = _txtServerUrl.Text.TrimEnd('/');
                await _client.SendRunApprovalAsync(serverUrl, msg.RunId, false);
            }
            catch { /* 서버 통보 실패는 무시 */ }

            _txtStatus.Text = "변경이 거부되었습니다.";
        }

        private async Task<string?> GetSolutionPathAsync()
        {
            try
            {
                var solution = await VS.Solutions.GetCurrentSolutionAsync();
                return solution?.FullPath;
            }
            catch
            {
                return null;
            }
        }

        // ── 채팅 메시지 렌더링 ───────────────────────────────────

        private void AddSystemMessage(string text)
        {
            var msg = new ChatMessageViewModel
            {
                Role = ChatMessageRole.System,
                Content = text
            };
            _messages.Add(msg);
            RenderMessage(msg);
        }

        private void AddUserMessage(string text)
        {
            var msg = new ChatMessageViewModel
            {
                Role = ChatMessageRole.User,
                Content = text
            };
            _messages.Add(msg);
            RenderMessage(msg);
        }

        private ChatMessageViewModel AddAssistantMessage(string text)
        {
            var msg = new ChatMessageViewModel
            {
                Role = ChatMessageRole.Assistant,
                Content = text
            };
            _messages.Add(msg);
            RenderMessage(msg);
            return msg;
        }

        private void RemoveMessage(ChatMessageViewModel msg)
        {
            _messages.Remove(msg);
            if (msg.Tag is UIElement element)
                _chatPanel.Children.Remove(element);
        }

        private void RenderMessage(ChatMessageViewModel msg)
        {
            var (fg, bg) = GetThemeColors();
            bool isDark = IsDark(bg);

            var border = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 4, 0, 4),
                MaxWidth = 600
            };

            msg.Tag = border;

            switch (msg.Role)
            {
                case ChatMessageRole.User:
                    border.Background = new SolidColorBrush(isDark
                        ? Color.FromRgb(40, 60, 90)
                        : Color.FromRgb(220, 235, 255));
                    border.HorizontalAlignment = HorizontalAlignment.Right;
                    var userText = new TextBlock
                    {
                        Text = msg.Content,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(fg)
                    };
                    border.Child = userText;
                    break;

                case ChatMessageRole.Assistant:
                    border.Background = new SolidColorBrush(isDark
                        ? Color.FromRgb(45, 45, 50)
                        : Color.FromRgb(243, 243, 243));
                    border.HorizontalAlignment = HorizontalAlignment.Left;

                    var assistantPanel = new StackPanel();

                    if (!string.IsNullOrEmpty(msg.IntentSummary))
                    {
                        var intentBlock = new TextBlock
                        {
                            Text = "\uD83D\uDD0D " + msg.IntentSummary,
                            FontStyle = FontStyles.Italic,
                            Foreground = new SolidColorBrush(isDark
                                ? Color.FromRgb(86, 156, 214)
                                : Color.FromRgb(0, 102, 153)),
                            Margin = new Thickness(0, 0, 0, 6),
                            TextWrapping = TextWrapping.Wrap
                        };
                        assistantPanel.Children.Add(intentBlock);
                    }

                    try
                    {
                        var flowDoc = MarkdownToFlowDocument.Convert(msg.Content, fg, bg);
                        var docViewer = new FlowDocumentScrollViewer
                        {
                            Document = flowDoc,
                            IsToolBarVisible = false,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                            Background = Brushes.Transparent,
                            BorderThickness = new Thickness(0)
                        };
                        assistantPanel.Children.Add(docViewer);
                    }
                    catch
                    {
                        var plainText = new TextBlock
                        {
                            Text = msg.Content,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = new SolidColorBrush(fg)
                        };
                        assistantPanel.Children.Add(plainText);
                    }

                    if (msg.HasCodeChange && msg.CodeChange != null)
                    {
                        assistantPanel.Children.Add(CreateDiffView(msg, fg, bg, isDark));
                        assistantPanel.Children.Add(CreateApprovalButtons(msg));
                    }

                    border.Child = assistantPanel;
                    break;

                case ChatMessageRole.System:
                    border.Background = new SolidColorBrush(isDark
                        ? Color.FromRgb(35, 35, 40)
                        : Color.FromRgb(248, 248, 248));
                    border.HorizontalAlignment = HorizontalAlignment.Center;
                    border.Opacity = 0.8;
                    var sysText = new TextBlock
                    {
                        Text = "\u2139 " + msg.Content,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(fg),
                        FontStyle = FontStyles.Italic
                    };
                    border.Child = sysText;
                    break;
            }

            _chatPanel.Children.Add(border);
            _chatScroll.ScrollToEnd();
        }

        // ── Side-by-side Diff 뷰 ────────────────────────────────

        private UIElement CreateDiffView(ChatMessageViewModel msg, Color fg, Color bg, bool isDark)
        {
            // v2.2: 멀티 파일인 경우 각 파일을 Expander로 표시
            if (msg.CodeChange?.IsMultiFile == true && msg.CodeChange.Files != null)
            {
                var container = new StackPanel { Margin = new Thickness(0, 8, 0, 4) };
                foreach (var fc in msg.CodeChange.Files)
                {
                    var expander = new Expander
                    {
                        IsExpanded = true,
                        Margin = new Thickness(0, 0, 0, 4)
                    };

                    // B-1: 파일별 체크박스 헤더
                    var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
                    var fileCheckbox = new CheckBox
                    {
                        IsChecked = fc.IsSelected,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 6, 0)
                    };
                    var capturedFc = fc;
                    fileCheckbox.Checked += (s, e) => capturedFc.IsSelected = true;
                    fileCheckbox.Unchecked += (s, e) => capturedFc.IsSelected = false;
                    headerPanel.Children.Add(fileCheckbox);
                    headerPanel.Children.Add(new TextBlock
                    {
                        Text = (fc.IsNewFile ? "[새 파일] " : "") + fc.FilePath,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(fg),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    expander.Header = headerPanel;

                    var fakeMsg = new ChatMessageViewModel
                    {
                        CodeChange = new CodeChangeInfo
                        {
                            Original      = fc.Original,
                            Modified      = fc.Modified,
                            SelectionOnly = fc.SelectionOnly,
                            // A-2: 서버 사전 계산 hunks 전달 (null이면 CreateSingleFileDiffView 내부에서 계산)
                            HunkSelections = fc.HunkSelections
                        }
                    };
                    expander.Content = CreateSingleFileDiffView(fakeMsg, fg, bg, isDark);
                    // A-2/A-1 버그 수정: 멀티파일에서 fakeMsg HunkSelections → fc.HunkSelections 동기화
                    fc.HunkSelections = fakeMsg.CodeChange?.HunkSelections;
                    container.Children.Add(expander);
                }
                return container;
            }

            return CreateSingleFileDiffView(msg, fg, bg, isDark);
        }

        private UIElement CreateSingleFileDiffView(ChatMessageViewModel msg, Color fg, Color bg, bool isDark)
        {
            var original = msg.CodeChange?.Original ?? "";
            var modified = msg.CodeChange?.Modified ?? "";
            var monoFont = new FontFamily("Consolas, Courier New, monospace");

            if (string.IsNullOrEmpty(original) && string.IsNullOrEmpty(modified))
            {
                var tb = new TextBlock { Text = "(코드 없음)", FontFamily = monoFont };
                tb.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
                return tb;
            }

            // A-2: 서버 사전 계산 hunks가 있으면 재계산 생략
            List<DiffHunk> hunks;
            if (msg.CodeChange?.HunkSelections != null && msg.CodeChange.HunkSelections.Count > 0)
            {
                hunks = msg.CodeChange.HunkSelections.Select(hs => hs.Hunk).ToList();
            }
            else
            {
                hunks = LineDiffEngine.Compute(original, modified).ToList();
                // A-1: HunkSelections 초기화
                if (msg.CodeChange != null && hunks.Count > 0)
                {
                    msg.CodeChange.HunkSelections = hunks.Select((h, i) =>
                        new HunkSelection { HunkIndex = i, IsAccepted = true, Hunk = h }).ToList();
                }
            }

            var origLines = SplitDisplayLines(original);
            var contentPanel = new StackPanel();

            if (hunks.Count == 0)
            {
                var noChangeTb = new TextBlock { Text = "변경 없음 — 원본과 동일합니다.", FontSize = 11, Margin = new Thickness(4) };
                noChangeTb.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
                contentPanel.Children.Add(noChangeTb);
                return contentPanel;
            }

            // 요약 헤더
            int totalAdded = hunks.Sum(h => SplitDisplayLines(h.NewText).Length);
            int totalDeleted = hunks.Sum(h => h.OriginalEnd - h.OriginalStart);
            var summary = new TextBlock
            {
                Text = $"변경: {hunks.Count}개 hunk  +{totalAdded} / -{totalDeleted} 라인",
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 4)
            };
            summary.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            contentPanel.Children.Add(summary);

            // A-3 색상
            const int CtxLines = 3;
            var codeBg    = new SolidColorBrush(isDark ? Color.FromRgb(22, 22, 26)  : Color.FromRgb(252, 252, 252));
            var deletedBg = new SolidColorBrush(isDark ? Color.FromRgb(80, 18, 18)  : Color.FromRgb(255, 214, 214));
            var addedBg   = new SolidColorBrush(isDark ? Color.FromRgb(18, 58, 18)  : Color.FromRgb(214, 255, 214));
            var hunkHeaderBg = new SolidColorBrush(isDark ? Color.FromRgb(30, 40, 55) : Color.FromRgb(218, 232, 252));
            var borderBrush  = new SolidColorBrush(isDark ? Color.FromRgb(55, 55, 65) : Color.FromRgb(200, 200, 210));

            for (int hi = 0; hi < hunks.Count; hi++)
            {
                var hunk = hunks[hi];
                var sel  = msg.CodeChange?.HunkSelections?.Count > hi
                    ? msg.CodeChange.HunkSelections[hi] : null;

                int addedCnt   = SplitDisplayLines(hunk.NewText).Length;
                int deletedCnt = hunk.OriginalEnd - hunk.OriginalStart;

                // ── A-1: hunk 헤더 + 체크박스 ─────────────────
                var hunkHeader = new Border
                {
                    Background = hunkHeaderBg,
                    Padding = new Thickness(6, 3, 6, 3),
                    Margin = new Thickness(0, hi == 0 ? 0 : 8, 0, 0),
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(1, 1, 1, 0)
                };
                var headerRow = new StackPanel { Orientation = Orientation.Horizontal };

                var hunkCb = new CheckBox
                {
                    IsChecked = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                hunkCb.SetResourceReference(Control.ForegroundProperty, VsBrushes.ToolWindowTextKey);
                if (sel != null)
                {
                    hunkCb.Checked   += (s, e) => sel.IsAccepted = true;
                    hunkCb.Unchecked += (s, e) => sel.IsAccepted = false;
                }
                headerRow.Children.Add(hunkCb);

                var hunkLabel = new TextBlock
                {
                    Text = $"Hunk {hi + 1}  @@ -{hunk.OriginalStart + 1},{deletedCnt} +{hunk.OriginalStart + 1},{addedCnt} @@",
                    FontFamily = monoFont,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                hunkLabel.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
                headerRow.Children.Add(hunkLabel);
                hunkHeader.Child = headerRow;
                contentPanel.Children.Add(hunkHeader);

                // ── A-3: 컬러 라인 패널 ───────────────────────
                var linesPanel = new StackPanel { Background = codeBg };

                // 컨텍스트 앞
                int ctxStart = Math.Max(0, hunk.OriginalStart - CtxLines);
                for (int i = ctxStart; i < hunk.OriginalStart && i < origLines.Length; i++)
                    linesPanel.Children.Add(MakeDiffLineTb(" " + origLines[i], codeBg, fg, monoFont));

                // 삭제 라인 (빨간)
                for (int i = hunk.OriginalStart; i < hunk.OriginalEnd && i < origLines.Length; i++)
                    linesPanel.Children.Add(MakeDiffLineTb("-" + origLines[i], deletedBg, fg, monoFont));

                // 추가 라인 (초록)
                foreach (var al in SplitDisplayLines(hunk.NewText))
                    linesPanel.Children.Add(MakeDiffLineTb("+" + al, addedBg, fg, monoFont));

                // 컨텍스트 뒤
                int ctxEnd = Math.Min(origLines.Length, hunk.OriginalEnd + CtxLines);
                for (int i = hunk.OriginalEnd; i < ctxEnd; i++)
                    linesPanel.Children.Add(MakeDiffLineTb(" " + origLines[i], codeBg, fg, monoFont));

                contentPanel.Children.Add(new Border
                {
                    BorderThickness = new Thickness(1, 0, 1, 1),
                    BorderBrush = borderBrush,
                    Child = linesPanel
                });
            }

            return new ScrollViewer
            {
                Content = contentPanel,
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }

        private static TextBlock MakeDiffLineTb(string text, SolidColorBrush bgBrush, Color fg, FontFamily font)
        {
            return new TextBlock
            {
                Text = text.TrimEnd('\r', '\n'),
                FontFamily = font,
                FontSize = 11,
                TextWrapping = TextWrapping.NoWrap,
                Padding = new Thickness(4, 1, 4, 1),
                Background = bgBrush,
                Foreground = new SolidColorBrush(fg)
            };
        }

        private static string[] SplitDisplayLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
            return text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        }

        // ── 확인/거부 버튼 ───────────────────────────────────────

        private UIElement CreateApprovalButtons(ChatMessageViewModel msg)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 0)
            };

            var btnApprove = new Button
            {
                Content = "\u2705 확인",
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 8, 0)
            };
            btnApprove.SetResourceReference(Control.BackgroundProperty, VsBrushes.ButtonFaceKey);
            btnApprove.SetResourceReference(Control.ForegroundProperty, VsBrushes.ButtonTextKey);
            btnApprove.SetResourceReference(Control.BorderBrushProperty, VsBrushes.ActiveBorderKey);
            btnApprove.Click += (s, e) =>
            {
#pragma warning disable VSSDK007
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ApproveRunChangeAsync(msg);
                });
#pragma warning restore VSSDK007
            };
            panel.Children.Add(btnApprove);

            var btnReject = new Button
            {
                Content = "\u274C 거부",
                Padding = new Thickness(10, 4, 10, 4)
            };
            btnReject.SetResourceReference(Control.BackgroundProperty, VsBrushes.ButtonFaceKey);
            btnReject.SetResourceReference(Control.ForegroundProperty, VsBrushes.ButtonTextKey);
            btnReject.SetResourceReference(Control.BorderBrushProperty, VsBrushes.ActiveBorderKey);
            btnReject.Click += (s, e) =>
            {
#pragma warning disable VSSDK007
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await RejectRunChangeAsync(msg);
                });
#pragma warning restore VSSDK007
            };
            panel.Children.Add(btnReject);

            msg.ApprovalPanel = panel;

            return panel;
        }

        private void UpdateApprovalUI(ChatMessageViewModel msg)
        {
            if (msg.ApprovalPanel is StackPanel panel)
            {
                foreach (var child in panel.Children.OfType<Button>())
                    child.IsEnabled = false;

                var stateText = new TextBlock
                {
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                stateText.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

                if (msg.Approval == ApprovalState.Approved)
                    stateText.Text = "\u2705 적용 완료";
                else if (msg.Approval == ApprovalState.Rejected)
                    stateText.Text = "\u274C 거부됨";

                panel.Children.Add(stateText);
            }
        }

        // ── VS 테마 색상 ─────────────────────────────────────────

        private (Color fg, Color bg) GetThemeColors()
        {
            try
            {
                var bgSystem = VSColorTheme.GetThemedColor(
                    EnvironmentColors.ToolWindowBackgroundColorKey);
                var fgSystem = VSColorTheme.GetThemedColor(
                    EnvironmentColors.ToolWindowTextColorKey);

                return (
                    Color.FromArgb(fgSystem.A, fgSystem.R, fgSystem.G, fgSystem.B),
                    Color.FromArgb(bgSystem.A, bgSystem.R, bgSystem.G, bgSystem.B)
                );
            }
            catch
            {
                return (
                    Color.FromRgb(220, 220, 220),
                    Color.FromRgb(30, 30, 30)
                );
            }
        }

        private static bool IsDark(Color c)
        {
            double brightness = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            return brightness < 0.5;
        }

        // ── Diff 적용 유틸리티 ─────────────────────────────────

        /// <summary>
        /// v2.2: 새 파일을 생성하고 에디터에서 엽니다.
        /// </summary>
        private static async Task CreateNewFileAsync(string filePath, string content)
        {
            var dir = System.IO.Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(filePath, content, System.Text.Encoding.UTF8);
            await VS.Documents.OpenAsync(filePath);
        }

        /// <summary>
        /// v2.2: FileChangeInfo를 기반으로 대상 파일을 열고 hunk를 적용합니다.
        /// </summary>
        private async Task ApplyHunksToFileAsync(FileChangeInfo fc)
        {
            var docView = await VS.Documents.OpenAsync(fc.FilePath);
            if (docView?.TextBuffer == null)
                throw new InvalidOperationException($"파일을 열 수 없습니다: {fc.FilePath}");

            var snapshot = docView.TextBuffer.CurrentSnapshot;
            var modifiedCode = NormalizeNewlines(fc.Modified, snapshot);
            modifiedCode = NormalizeIndentation(modifiedCode, snapshot);

            string originalText;
            int selectionOffset = -1;
            if (fc.SelectionOnly && !string.IsNullOrEmpty(fc.Original))
            {
                originalText = NormalizeNewlines(fc.Original!, snapshot);
                var fullText = snapshot.GetText();
                selectionOffset = fullText.IndexOf(originalText, StringComparison.Ordinal);
                if (selectionOffset < 0)
                    throw new InvalidOperationException("원본 텍스트 매칭 실패");
            }
            else
            {
                originalText = snapshot.GetText();
            }

            var allHunks = LineDiffEngine.Compute(originalText, modifiedCode).ToList();

            // A-1: 파일별 HunkSelections이 있으면 선택된 hunk만 적용
            List<DiffHunk> hunksToApply;
            if (fc.HunkSelections != null && fc.HunkSelections.Count == allHunks.Count)
                hunksToApply = allHunks.Where((h, i) => fc.HunkSelections[i].IsAccepted).ToList();
            else
                hunksToApply = allHunks;

            if (hunksToApply.Count == 0) return;

            // LineDiffEngine과 동일한 방식으로 라인 분할 (각 라인에 \n 포함)
            var origLines = SplitToLinesList(originalText);

            // 역순으로 적용하여 앞쪽 라인 인덱스를 보존
            foreach (var hunk in System.Linq.Enumerable.OrderByDescending(hunksToApply, h => h.OriginalStart))
            {
                var newLines = SplitToLinesList(hunk.NewText);
                int removeCount = Math.Min(
                    hunk.OriginalEnd - hunk.OriginalStart,
                    origLines.Count - hunk.OriginalStart);
                if (removeCount > 0)
                    origLines.RemoveRange(hunk.OriginalStart, removeCount);
                origLines.InsertRange(hunk.OriginalStart, newLines);
            }

            var resultText = string.Concat(origLines);

            using (var edit = docView.TextBuffer.CreateEdit())
            {
                if (selectionOffset >= 0)
                    edit.Replace(new Microsoft.VisualStudio.Text.Span(selectionOffset, originalText.Length), resultText);
                else
                    edit.Replace(new Microsoft.VisualStudio.Text.Span(0, snapshot.Length), resultText);
                edit.Apply();
            }
        }

        /// <summary>
        /// LineDiffEngine.SplitLines와 동일한 방식으로 텍스트를 라인 목록으로 분할한다.
        /// 각 라인은 줄바꿈 문자(\n)를 포함하며, 마지막 라인에 \n이 없으면 그대로 포함한다.
        /// </summary>
        private static List<string> SplitToLinesList(string text)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    lines.Add(text.Substring(start, i - start + 1));
                    start = i + 1;
                }
            }
            if (start < text.Length)
                lines.Add(text.Substring(start));
            return lines;
        }

        // ── 코드 정규화 유틸리티 ─────────────────────────────────

        private static string NormalizeNewlines(string code, Microsoft.VisualStudio.Text.ITextSnapshot snapshot)
        {
            string docNewline = "\r\n";
            if (snapshot.LineCount > 1)
            {
                var firstLine = snapshot.GetLineFromLineNumber(0);
                var lineBreakText = snapshot.GetText(firstLine.End, firstLine.LineBreakLength);
                if (!string.IsNullOrEmpty(lineBreakText))
                    docNewline = lineBreakText;
            }

            code = code.Replace("\r\n", "\n").Replace("\r", "\n");
            if (docNewline != "\n")
                code = code.Replace("\n", docNewline);

            return code;
        }

        private static string NormalizeIndentation(string code, Microsoft.VisualStudio.Text.ITextSnapshot snapshot)
        {
            // 문서의 줄바꿈 형식을 감지하여 동일하게 사용
            string docNewline = "\r\n";
            if (snapshot.LineCount > 1)
            {
                var firstLine = snapshot.GetLineFromLineNumber(0);
                var lineBreakText = snapshot.GetText(firstLine.End, firstLine.LineBreakLength);
                if (!string.IsNullOrEmpty(lineBreakText))
                    docNewline = lineBreakText;
            }

            int tabLines = 0;
            int spaceLines = 0;
            int lineCount = Math.Min(snapshot.LineCount, 100);
            for (int i = 0; i < lineCount; i++)
            {
                var line = snapshot.GetLineFromLineNumber(i);
                var text = line.GetText();
                if (text.Length == 0) continue;
                if (text[0] == '\t') tabLines++;
                else if (text[0] == ' ' && text.Length >= 2) spaceLines++;
            }

            bool originalUsesTabs = tabLines > spaceLines;

            var codeLines = code.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            int llmTabLines = 0;
            int llmSpaceLines = 0;
            foreach (var cl in codeLines)
            {
                if (cl.Length == 0) continue;
                if (cl[0] == '\t') llmTabLines++;
                else if (cl[0] == ' ' && cl.Length >= 2) llmSpaceLines++;
            }

            bool llmUsesTabs = llmTabLines > llmSpaceLines;
            if (originalUsesTabs == llmUsesTabs) return code;

            if (originalUsesTabs && !llmUsesTabs)
            {
                int indentUnit = DetectSpaceIndentUnit(codeLines);
                if (indentUnit <= 0) return code;

                var sb = new System.Text.StringBuilder(code.Length);
                for (int i = 0; i < codeLines.Length; i++)
                {
                    if (i > 0) sb.Append(docNewline);
                    var line = codeLines[i];
                    int spaces = 0;
                    while (spaces < line.Length && line[spaces] == ' ') spaces++;
                    int tabCount = spaces / indentUnit;
                    int remainder = spaces % indentUnit;
                    sb.Append('\t', tabCount);
                    sb.Append(' ', remainder);
                    sb.Append(line, spaces, line.Length - spaces);
                }
                return sb.ToString();
            }
            else
            {
                int indentUnit = 4;
                var origLines = new string[Math.Min(snapshot.LineCount, 100)];
                for (int i = 0; i < origLines.Length; i++)
                    origLines[i] = snapshot.GetLineFromLineNumber(i).GetText();
                int detected = DetectSpaceIndentUnit(origLines);
                if (detected > 0) indentUnit = detected;

                var sb = new System.Text.StringBuilder(code.Length * 2);
                for (int i = 0; i < codeLines.Length; i++)
                {
                    if (i > 0) sb.Append(docNewline);
                    var line = codeLines[i];
                    int tabs = 0;
                    while (tabs < line.Length && line[tabs] == '\t') tabs++;
                    sb.Append(' ', tabs * indentUnit);
                    sb.Append(line, tabs, line.Length - tabs);
                }
                return sb.ToString();
            }
        }

        private static int DetectSpaceIndentUnit(string[] lines)
        {
            int gcd = 0;
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                int spaces = 0;
                while (spaces < line.Length && line[spaces] == ' ') spaces++;
                if (spaces < 2) continue;
                gcd = gcd == 0 ? spaces : Gcd(gcd, spaces);
            }
            return gcd > 0 ? gcd : 4;
        }

        private static int Gcd(int a, int b)
        {
            while (b != 0) { int t = b; b = a % b; a = t; }
            return a;
        }

        // ── 대화 세션 백업/복원 ──────────────────────────────────

        private void BackupCurrentSession()
        {
            // 사용자 메시지가 없으면 백업 불필요
            if (!_messages.Any(m => m.Role == ChatMessageRole.User))
                return;

            // 제목: 첫 번째 사용자 메시지에서 추출
            var firstUserMsg = _messages.FirstOrDefault(m => m.Role == ChatMessageRole.User);
            var title = firstUserMsg?.Content ?? "(대화)";
            if (title.Length > 40)
                title = title.Substring(0, 40) + "...";

            // UI 참조는 직렬화 불가이므로 제거한 복사본 생성
            var snapshot = _messages.Select(m => new ChatMessageViewModel
            {
                Role = m.Role,
                Content = m.Content,
                Timestamp = m.Timestamp,
                HasCodeChange = m.HasCodeChange,
                CodeChange = m.CodeChange,
                Approval = m.Approval,
                IntentSummary = m.IntentSummary
            }).ToList();

            var session = new ChatSession
            {
                ConversationId = _conversationId ?? "",
                Title = title,
                CreatedAt = _messages.FirstOrDefault()?.Timestamp ?? DateTime.Now,
                Messages = snapshot
            };

            // 동일 conversationId의 기존 백업이 있으면 갱신
            var existing = _chatSessions.FindIndex(s => s.ConversationId == _conversationId && _conversationId != null);
            if (existing >= 0)
                _chatSessions[existing] = session;
            else
                _chatSessions.Insert(0, session);

            // 최대 개수 제한
            while (_chatSessions.Count > MaxSessionHistory)
                _chatSessions.RemoveAt(_chatSessions.Count - 1);

            RefreshHistoryComboBox();
        }

        private void RestoreSession(ChatSession session)
        {
            _conversationId = session.ConversationId;
            _messages.Clear();
            _chatPanel.Children.Clear();

            foreach (var msg in session.Messages)
            {
                // UI 참조를 새로 생성하여 복원
                var restored = new ChatMessageViewModel
                {
                    Role = msg.Role,
                    Content = msg.Content,
                    Timestamp = msg.Timestamp,
                    HasCodeChange = msg.HasCodeChange,
                    CodeChange = msg.CodeChange,
                    Approval = msg.Approval,
                    IntentSummary = msg.IntentSummary
                };

                _messages.Add(restored);
                RenderMessage(restored);

                // 승인/거부 상태가 있는 메시지는 버튼을 비활성화
                if (restored.HasCodeChange && restored.Approval != ApprovalState.Pending)
                    UpdateApprovalUI(restored);
            }

            _txtStatus.Text = string.Format("이전 대화 복원 ({0:HH:mm})", session.CreatedAt);
        }

        private void RefreshHistoryComboBox()
        {
            _cmbHistory.Items.Clear();
            foreach (var session in _chatSessions)
                _cmbHistory.Items.Add(session);
        }

        // ── 열린 파일 수집 ───────────────────────────────────────

        /// <summary>
        /// VS 편집기에서 현재 열려 있는 코드 파일들을 수집하여 RunFileContextDto 배열로 반환한다.
        /// IVsRunningDocumentTable을 통해 열린 문서 목록을 열거한다.
        /// </summary>
        private async Task<RunFileContextDto[]> CollectOpenFilesAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var rdt = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SVsRunningDocumentTable))
                          as IVsRunningDocumentTable;
                if (rdt == null) return Array.Empty<RunFileContextDto>();

                rdt.GetRunningDocumentsEnum(out var enumerator);
                var cookies = new uint[32];
                var files = new List<RunFileContextDto>();

                int hr;
                do
                {
                    hr = enumerator.Next((uint)cookies.Length, cookies, out var fetched);
                    if (fetched == 0) break;

                    for (uint i = 0; i < fetched; i++)
                    {
                        rdt.GetDocumentInfo(cookies[i], out _, out _, out _,
                            out string moniker, out _, out _, out _);

                        if (string.IsNullOrEmpty(moniker) || !System.IO.File.Exists(moniker))
                            continue;

                        var ext = System.IO.Path.GetExtension(moniker).ToLowerInvariant();
                        if (!IsCodeFileExtension(ext)) continue;

                        try
                        {
                            var content = System.IO.File.ReadAllText(moniker);
                            files.Add(new RunFileContextDto
                            {
                                FilePath = moniker,
                                Code = content,
                                Language = LanguageDetector.FromFilePath(moniker)
                            });
                        }
                        catch { /* 읽기 불가 파일은 무시 */ }
                    }
                } while (hr == Microsoft.VisualStudio.VSConstants.S_OK);

                // B-2: 사용자가 명시적으로 파일을 선택했으면 필터링
                if (_selectedFilePaths.Count > 0)
                    files = files.Where(f => _selectedFilePaths.Contains(f.FilePath)).ToList();

                return files.ToArray();
            }
            catch
            {
                return Array.Empty<RunFileContextDto>();
            }
        }

        private static bool IsCodeFileExtension(string ext) =>
            ext is ".cs" or ".vb" or ".fs" or ".ts" or ".tsx" or ".js" or ".jsx"
                or ".py" or ".java" or ".cpp" or ".c" or ".h" or ".go" or ".rs"
                or ".xaml" or ".html" or ".css" or ".json" or ".yaml" or ".yml"
                or ".md" or ".sql";

        // ── 유틸리티 ─────────────────────────────────────────────

        private void SetBusy(bool busy)
        {
            _isBusy = busy;
            _btnSend.IsEnabled = !busy;
            _txtInput.IsEnabled = !busy;
            _btnNewChat.IsEnabled = !busy;
        }
    }
}
