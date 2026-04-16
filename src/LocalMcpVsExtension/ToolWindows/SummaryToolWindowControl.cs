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
        private string _conversationId;
        private string _currentRunId;
        private CancellationTokenSource _pollCts;
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
            inputInner.Children.Add(_chkIncludeCode);

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

        private Button CreateButton(string content, string tooltip = null)
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

            _conversationId = null;
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

        private void TxtInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                e.Handled = true;
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await SendMessageAsync();
                });
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

            AddUserMessage(message);

            try
            {
                string code = null;
                string language = null;
                bool selectionOnly = false;
                string activeFilePath = null;

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
                var startReq = new RunStartRequest
                {
                    Message = message,
                    Code = code,
                    Language = language,
                    SelectionOnly = selectionOnly,
                    ConversationId = _conversationId,
                    ActiveFilePath = activeFilePath
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
                        var content = snapshot.FinalSummary ?? snapshot.Proposal?.Summary ?? "(결과 없음)";
                        if (!string.IsNullOrEmpty(snapshot.Error))
                            content = "오류: " + snapshot.Error;

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
                        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        MaxHeight = 400,
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

                // §8g: 적용 실패 분기 — 원본 매칭 실패 시 build_test Skipped
                var applyFailed = false;
                string applyErrorMsg = null;

                try
                {
                    using (var edit = docView.TextBuffer.CreateEdit())
                    {
                        if (msg.CodeChange.SelectionOnly && !string.IsNullOrEmpty(msg.CodeChange.Original))
                        {
                            var fullText = snapshot.GetText();
                            var normalizedOriginal = NormalizeNewlines(msg.CodeChange.Original, snapshot);
                            int idx = fullText.IndexOf(normalizedOriginal, StringComparison.Ordinal);
                            if (idx >= 0)
                            {
                                edit.Replace(idx, normalizedOriginal.Length, modifiedCode);
                                edit.Apply();
                            }
                            else
                            {
                                applyFailed = true;
                                applyErrorMsg = "원본 텍스트 매칭 실패: 문서가 변경되었을 수 있습니다.";
                                // using 블록 종료 시 자동 취소
                            }
                        }
                        else
                        {
                            edit.Replace(0, snapshot.Length, modifiedCode);
                            edit.Apply();
                        }
                    }
                }
                catch (Exception applyEx)
                {
                    applyFailed = true;
                    applyErrorMsg = "코드 적용 중 오류: " + applyEx.Message;
                }

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
                        Build = new ClientBuildResult { Attempted = false, Summary = "적용 실패로 빌드 생략" },
                        Tests = new ClientTestResult { Attempted = false, Summary = "적용 실패로 테스트 생략" }
                    };
                }
                else
                {
                    _txtStatus.Text = "적용 완료. 빌드/테스트 실행 중...";
                    SetBusy(true);

                    // 빌드/테스트 실행
                    clientResult = new ClientResultRequest { Applied = true };
                    var solutionPath = await GetSolutionPathAsync();
                    if (!string.IsNullOrEmpty(solutionPath))
                    {
                        var buildResult = await _buildRunner.BuildAsync(solutionPath);
                        clientResult.Build = new ClientBuildResult
                        {
                            Attempted = buildResult.Attempted,
                            Succeeded = buildResult.Succeeded,
                            Summary = buildResult.Summary
                        };

                        if (buildResult.Succeeded == true)
                        {
                            var testResult = await _buildRunner.TestAsync(solutionPath);
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

        private async Task<string> GetSolutionPathAsync()
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
                            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                            MaxHeight = 400,
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
            var diffGrid = new Grid { Margin = new Thickness(0, 8, 0, 4) };
            diffGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            diffGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            diffGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var monoFont = new FontFamily("Consolas, Courier New, monospace");
            var codeBg = new SolidColorBrush(isDark
                ? Color.FromRgb(30, 30, 34)
                : Color.FromRgb(250, 250, 250));

            // 원본 (좌측)
            var originalPanel = new StackPanel();
            originalPanel.Children.Add(new TextBlock
            {
                Text = "\u25C0 원본",
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(fg),
                Margin = new Thickness(0, 0, 0, 4)
            });
            var originalBox = new TextBox
            {
                Text = msg.CodeChange?.Original ?? "",
                IsReadOnly = true,
                FontFamily = monoFont,
                FontSize = 11,
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 250,
                Background = codeBg,
                Foreground = new SolidColorBrush(fg),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4)
            };
            originalBox.SetResourceReference(Control.BorderBrushProperty, VsBrushes.ActiveBorderKey);
            originalPanel.Children.Add(originalBox);
            Grid.SetColumn(originalPanel, 0);
            diffGrid.Children.Add(originalPanel);

            // 구분선
            var separator = new Border
            {
                Width = 2,
                Background = new SolidColorBrush(isDark
                    ? Color.FromRgb(60, 60, 65)
                    : Color.FromRgb(200, 200, 200))
            };
            Grid.SetColumn(separator, 1);
            diffGrid.Children.Add(separator);

            // 변경 (우측)
            var modifiedPanel = new StackPanel();
            modifiedPanel.Children.Add(new TextBlock
            {
                Text = "\u25B6 변경",
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(isDark
                    ? Color.FromRgb(78, 201, 176)
                    : Color.FromRgb(0, 128, 0)),
                Margin = new Thickness(0, 0, 0, 4)
            });
            var modifiedBox = new TextBox
            {
                Text = msg.CodeChange?.Modified ?? "",
                IsReadOnly = true,
                FontFamily = monoFont,
                FontSize = 11,
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 250,
                Background = codeBg,
                Foreground = new SolidColorBrush(fg),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4)
            };
            modifiedBox.SetResourceReference(Control.BorderBrushProperty, VsBrushes.ActiveBorderKey);
            modifiedPanel.Children.Add(modifiedBox);
            Grid.SetColumn(modifiedPanel, 2);
            diffGrid.Children.Add(modifiedPanel);

            return diffGrid;
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
                ConversationId = _conversationId,
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
