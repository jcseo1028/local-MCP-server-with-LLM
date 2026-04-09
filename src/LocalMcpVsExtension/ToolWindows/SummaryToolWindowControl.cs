using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Community.VisualStudio.Toolkit;
using LocalMcpVsExtension.Services;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace LocalMcpVsExtension.ToolWindows
{
    /// <summary>
    /// Local MCP 코드 요약 Tool Window의 WPF 컨트롤.
    /// VS 테마(Dark/Light) 자동 대응, Markdown 렌더링, 동적 도구 로딩을 지원한다.
    /// </summary>
    public sealed class SummaryToolWindowControl : UserControl
    {
        private readonly McpRestClient _client = new McpRestClient();

        // 수정 도구 — 결과를 에디터에 반영하기 위한 상태
        private string? _lastResult;
        private bool _lastSelectionOnly;

        // 수정 도구 목록 (결과를 에디터에 적용 가능한 도구)
        private static readonly HashSet<string> EditTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "add_comments", "refactor_current_code", "fix_code_issues"
        };

        // ── UI 요소 ────────────────────────────────────────────
        private readonly ComboBox _cmbTool;
        private readonly Button _btnRefresh;
        private readonly Button _btnCurrentFile;
        private readonly Button _btnSelection;
        private readonly Button _btnApply;
        private readonly TextBox _txtServerUrl;
        private readonly FlowDocumentScrollViewer _docViewer;
        private readonly TextBlock _txtStatus;
        private readonly TextBlock _txtLanguage;

        public SummaryToolWindowControl()
        {
            // ── Grid 레이아웃 (3행) ─────────────────────────────
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // VS 테마 배경
            grid.SetResourceReference(Panel.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);

            // ── Row 0: 도구 모음 ────────────────────────────────
            var toolbar = new WrapPanel { Margin = new Thickness(6, 6, 6, 4) };

            // 도구 선택
            toolbar.Children.Add(CreateLabel("도구:"));

            _cmbTool = new ComboBox
            {
                Width = 220,
                Margin = new Thickness(0, 0, 4, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            // VS 내장 테마 스타일 적용 — Dark/Light 모두 자동 대응
            _cmbTool.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.ComboBoxStyleKey);

            toolbar.Children.Add(_cmbTool);

            _btnRefresh = CreateButton("\u21BB", "도구 목록 새로고침");
            _btnRefresh.Width = 28;
            _btnRefresh.Margin = new Thickness(0, 0, 12, 4);
            _btnRefresh.Click += BtnRefresh_Click;
            toolbar.Children.Add(_btnRefresh);

            // 실행 버튼
            _btnCurrentFile = CreateButton("현재 파일");
            _btnCurrentFile.Click += BtnCurrentFile_Click;
            toolbar.Children.Add(_btnCurrentFile);

            _btnSelection = CreateButton("선택 영역");
            _btnSelection.Margin = new Thickness(0, 0, 4, 4);
            _btnSelection.Click += BtnSelection_Click;
            toolbar.Children.Add(_btnSelection);

            _btnApply = CreateButton("📋 적용", "결과를 에디터에 반영합니다");
            _btnApply.Margin = new Thickness(0, 0, 12, 4);
            _btnApply.IsEnabled = false;
            _btnApply.Click += BtnApply_Click;
            toolbar.Children.Add(_btnApply);

            // 서버 주소
            toolbar.Children.Add(CreateLabel("서버:"));

            _txtServerUrl = new TextBox
            {
                Text = "http://localhost:5100",
                Width = 180,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(0, 0, 0, 4)
            };
            _txtServerUrl.SetResourceReference(Control.BackgroundProperty, VsBrushes.WindowKey);
            _txtServerUrl.SetResourceReference(Control.ForegroundProperty, VsBrushes.WindowTextKey);
            _txtServerUrl.SetResourceReference(Control.BorderBrushProperty, VsBrushes.ActiveBorderKey);
            toolbar.Children.Add(_txtServerUrl);

            Grid.SetRow(toolbar, 0);
            grid.Children.Add(toolbar);

            // ── Row 1: Markdown 결과 영역 ──────────────────────
            _docViewer = new FlowDocumentScrollViewer
            {
                IsToolBarVisible = false,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                BorderThickness = new Thickness(0, 1, 0, 1)
            };
            _docViewer.SetResourceReference(Control.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
            _docViewer.SetResourceReference(Control.BorderBrushProperty, VsBrushes.ActiveBorderKey);
            _docViewer.SetResourceReference(Control.ForegroundProperty, VsBrushes.ToolWindowTextKey);

            ShowPlainText(
                "도구 창이 열렸습니다.\n" +
                "도구를 선택하고 버튼을 클릭하여 코드 요약을 시작하세요.\n\n" +
                "사전 조건:\n" +
                "  1. Ollama 실행 중 (ollama serve)\n" +
                "  2. MCP 서버 실행 중 (dotnet run)");

            Grid.SetRow(_docViewer, 1);
            grid.Children.Add(_docViewer);

            // ── Row 2: 상태 표시줄 ─────────────────────────────
            var statusBar = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(6, 4, 6, 4)
            };
            statusBar.SetResourceReference(Border.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
            statusBar.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ActiveBorderKey);

            var statusPanel = new DockPanel();

            _txtStatus = new TextBlock { Text = "준비" };
            _txtStatus.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            DockPanel.SetDock(_txtStatus, Dock.Left);
            statusPanel.Children.Add(_txtStatus);

            _txtLanguage = new TextBlock
            {
                Text = "",
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _txtLanguage.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            DockPanel.SetDock(_txtLanguage, Dock.Right);
            statusPanel.Children.Add(_txtLanguage);

            statusBar.Child = statusPanel;
            Grid.SetRow(statusBar, 2);
            grid.Children.Add(statusBar);

            Content = grid;

            // 시작 시 도구 목록 로드
            Loaded += OnLoaded;
        }

        // ── 테마 적용 헬퍼 ─────────────────────────────────────

        private TextBlock CreateLabel(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 4)
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
                Margin = new Thickness(0, 0, 4, 4),
                ToolTip = tooltip
            };
            btn.SetResourceReference(Control.BackgroundProperty, VsBrushes.ButtonFaceKey);
            btn.SetResourceReference(Control.ForegroundProperty, VsBrushes.ButtonTextKey);
            btn.SetResourceReference(Control.BorderBrushProperty, VsBrushes.ActiveBorderKey);
            return btn;
        }

        // ── 이벤트 핸들러 ──────────────────────────────────────

#pragma warning disable VSSDK007
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await LoadToolsAsync();
            });
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await LoadToolsAsync();
            });
        }

        private void BtnCurrentFile_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ExecuteToolAsync(selectionOnly: false);
            });
        }

        private void BtnSelection_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ExecuteToolAsync(selectionOnly: true);
            });
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ApplyResultToEditorAsync();
            });
        }
#pragma warning restore VSSDK007

        // ── 도구 목록 로드 ─────────────────────────────────────

        private async Task LoadToolsAsync()
        {
            try
            {
                _txtStatus.Text = "도구 목록 가져오는 중...";
                var serverUrl = _txtServerUrl.Text.TrimEnd('/');
                var tools = await _client.GetToolsAsync(serverUrl);

                _cmbTool.Items.Clear();
                foreach (var tool in tools)
                {
                    var item = new ComboBoxItem
                    {
                        Content = tool.Name,
                        ToolTip = tool.Description,
                        Tag = tool.Name
                    };
                    _cmbTool.Items.Add(item);
                }

                if (_cmbTool.Items.Count > 0)
                    _cmbTool.SelectedIndex = 0;

                _txtStatus.Text = $"도구 {tools.Length}개 로드 완료";
            }
            catch (Exception ex)
            {
                _txtStatus.Text = "도구 목록 로드 실패";
                ShowPlainText(
                    "도구 목록을 가져올 수 없습니다.\n\n" +
                    "MCP 서버가 실행 중인지 확인하세요:\n" +
                    "  cd src/LocalMcpServer && dotnet run\n\n" +
                    "오류: " + ex.Message);

                // 기본 도구를 폴백으로 추가
                _cmbTool.Items.Clear();
                _cmbTool.Items.Add(new ComboBoxItem
                {
                    Content = "summarize_current_code",
                    Tag = "summarize_current_code"
                });
                _cmbTool.SelectedIndex = 0;
            }
        }

        // ── 도구 실행 ──────────────────────────────────────────

        private async Task ExecuteToolAsync(bool selectionOnly)
        {
            try
            {
                SetBusy(true);
                _txtStatus.Text = "코드를 가져오는 중...";

                // 선택된 도구
                var selectedItem = _cmbTool.SelectedItem as ComboBoxItem;
                var toolName = selectedItem?.Tag?.ToString() ?? "summarize_current_code";

                // 활성 편집기에서 코드 획득
                var docView = await VS.Documents.GetActiveDocumentViewAsync();
                if (docView?.TextBuffer == null)
                {
                    ShowPlainText("열린 문서가 없습니다.");
                    _txtStatus.Text = "오류";
                    return;
                }

                string code;
                if (selectionOnly)
                {
                    var selection = docView.TextView?.Selection;
                    if (selection == null || selection.SelectedSpans.Count == 0)
                    {
                        ShowPlainText("선택된 텍스트가 없습니다.");
                        _txtStatus.Text = "오류";
                        return;
                    }
                    code = string.Join(Environment.NewLine,
                        selection.SelectedSpans.Select(span => span.GetText()));
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        ShowPlainText("선택된 텍스트가 없습니다.");
                        _txtStatus.Text = "오류";
                        return;
                    }
                }
                else
                {
                    code = docView.TextBuffer.CurrentSnapshot.GetText();
                }

                if (string.IsNullOrWhiteSpace(code))
                {
                    ShowPlainText("빈 문서입니다.");
                    _txtStatus.Text = "오류";
                    return;
                }

                // 언어 감지
                var filePath = docView.FilePath;
                var language = LanguageDetector.FromFilePath(filePath);
                _txtLanguage.Text = "언어: " + language;

                // REST 호출
                var serverUrl = _txtServerUrl.Text.TrimEnd('/');
                _txtStatus.Text = $"MCP 서버 요청 중 ({toolName})...";
                ShowPlainText(
                    "처리하고 있습니다.\n" +
                    "모델 크기에 따라 10~60초 소요될 수 있습니다...");

                var arguments = new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["language"] = language
                };

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await _client.CallToolAsync(serverUrl, toolName, arguments);
                sw.Stop();

                // Markdown 렌더링
                ShowMarkdown(result);

                // 수정 도구면 결과를 저장하고 적용 버튼 활성화
                if (EditTools.Contains(toolName))
                {
                    _lastResult = result;
                    _lastSelectionOnly = selectionOnly;
                    _btnApply.IsEnabled = true;
                }
                else
                {
                    _lastResult = null;
                    _btnApply.IsEnabled = false;
                }

                _txtStatus.Text = string.Format(
                    "완료 ({0:F1}초) — {1:HH:mm:ss}", sw.Elapsed.TotalSeconds, DateTime.Now);
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                ShowPlainText(
                    "MCP 서버에 연결할 수 없습니다.\n\n" +
                    "서버가 실행 중인지 확인하세요:\n" +
                    "  cd src/LocalMcpServer && dotnet run\n\n" +
                    "오류: " + ex.Message);
                _txtStatus.Text = "연결 실패";
            }
            catch (TaskCanceledException)
            {
                ShowPlainText(
                    "요청 시간이 초과되었습니다.\n" +
                    "Ollama가 실행 중인지 확인하세요.");
                _txtStatus.Text = "시간 초과";
            }
            catch (Exception ex)
            {
                ShowPlainText("오류가 발생했습니다:\n" + ex.Message);
                _txtStatus.Text = "오류";
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ── 에디터에 결과 반영 ─────────────────────────────────

        /// <summary>
        /// 수정 도구(add_comments, refactor, fix) 결과를 활성 에디터에 적용한다.
        /// LLM 응답에서 코드 블록을 추출하여 에디터 텍스트를 교체한다.
        /// </summary>
        private async Task ApplyResultToEditorAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_lastResult))
                {
                    _txtStatus.Text = "적용할 결과가 없습니다.";
                    return;
                }

                var docView = await VS.Documents.GetActiveDocumentViewAsync();
                if (docView?.TextBuffer == null)
                {
                    _txtStatus.Text = "열린 문서가 없습니다.";
                    return;
                }

                // LLM 응답에서 코드 추출 (코드 블록이 있으면 추출, 없으면 전체 텍스트)
                var code = ExtractCodeFromResult(_lastResult ?? "");
                if (string.IsNullOrWhiteSpace(code))
                {
                    _txtStatus.Text = "적용할 코드를 찾을 수 없습니다.";
                    return;
                }

                var textBuffer = docView.TextBuffer;

                var snapshot = textBuffer.CurrentSnapshot;

                // 원본 문서의 줄바꿈 형식을 감지하여 LLM 응답의 줄바꿈을 맞춘다
                code = NormalizeNewlines(code, snapshot);

                // 원본 문서의 들여쓰기 스타일(탭/스페이스)에 맞춰 변환한다
                code = NormalizeIndentation(code, snapshot);

                if (_lastSelectionOnly)
                {
                    // 선택 영역만 교체
                    var selection = docView.TextView?.Selection;
                    if (selection != null && selection.SelectedSpans.Count > 0)
                    {
                        var span = selection.SelectedSpans[0];
                        using (var edit = textBuffer.CreateEdit())
                        {
                            edit.Replace(span.Span, code);
                            edit.Apply();
                        }
                    }
                    else
                    {
                        _txtStatus.Text = "선택 영역이 없습니다. 동일한 영역을 다시 선택 후 적용하세요.";
                        return;
                    }
                }
                else
                {
                    // 전체 파일 교체
                    using (var edit = textBuffer.CreateEdit())
                    {
                        edit.Replace(0, snapshot.Length, code);
                        edit.Apply();
                    }
                }

                _btnApply.IsEnabled = false;
                _lastResult = null;
                _txtStatus.Text = "에디터에 적용 완료";
            }
            catch (Exception ex)
            {
                _txtStatus.Text = "적용 실패: " + ex.Message;
            }
        }

        /// <summary>
        /// LLM 응답에서 코드 부분을 추출한다.
        /// 마지막 코드 블록(``` 감싸인)이 있으면 해당 내용, 없으면 전체 텍스트를 반환한다.
        /// </summary>
        private static string ExtractCodeFromResult(string result)
        {
            // 마지막 코드 블록을 찾는다 (LLM이 "변경 요약" 뒤에 코드를 넣는 경우)
            var lastFenceStart = -1;
            var lastFenceEnd = -1;
            var idx = 0;

            while (idx < result.Length)
            {
                var fenceStart = result.IndexOf("```", idx, StringComparison.Ordinal);
                if (fenceStart < 0) break;

                // 여는 ``` 다음 줄
                var contentStart = result.IndexOf('\n', fenceStart);
                if (contentStart < 0) break;
                contentStart++;

                // 닫는 ```
                var fenceEnd = result.IndexOf("```", contentStart, StringComparison.Ordinal);
                if (fenceEnd < 0) break;

                lastFenceStart = contentStart;
                lastFenceEnd = fenceEnd;
                idx = fenceEnd + 3;
            }

            if (lastFenceStart >= 0 && lastFenceEnd > lastFenceStart)
            {
                return result.Substring(lastFenceStart, lastFenceEnd - lastFenceStart).TrimEnd();
            }

            // 코드 블록이 없으면 전체 텍스트 반환
            return result.Trim();
        }

        /// <summary>
        /// LLM 응답의 줄바꿈을 원본 문서의 줄바꿈 형식에 맞춘다.
        /// LLM은 \n만 반환하는 경우가 많지만, Windows 파일은 \r\n을 사용한다.
        /// </summary>
        private static string NormalizeNewlines(string code, Microsoft.VisualStudio.Text.ITextSnapshot snapshot)
        {
            // 원본 문서에서 줄바꿈 형식을 감지 (첫 번째 줄의 LineBreak 사용)
            string docNewline = "\r\n"; // 기본값은 Windows 줄바꿈
            if (snapshot.LineCount > 1)
            {
                var firstLine = snapshot.GetLineFromLineNumber(0);
                var lineBreakText = snapshot.GetText(firstLine.End, firstLine.LineBreakLength);
                if (!string.IsNullOrEmpty(lineBreakText))
                {
                    docNewline = lineBreakText;
                }
            }

            // LLM 응답의 줄바꿈을 먼저 \n으로 통일한 뒤, 원본 형식으로 변환
            code = code.Replace("\r\n", "\n").Replace("\r", "\n");
            if (docNewline != "\n")
            {
                code = code.Replace("\n", docNewline);
            }

            return code;
        }

        /// <summary>
        /// 원본 문서의 들여쓰기 스타일(탭 vs 스페이스)을 감지하고,
        /// LLM 응답의 들여쓰기를 원본 스타일에 맞춰 변환한다.
        /// </summary>
        private static string NormalizeIndentation(string code, Microsoft.VisualStudio.Text.ITextSnapshot snapshot)
        {
            // 원본 문서의 들여쓰기 스타일 감지: 탭 사용 줄 vs 스페이스 사용 줄 카운트
            int tabLines = 0;
            int spaceLines = 0;
            int lineCount = Math.Min(snapshot.LineCount, 100); // 최대 100줄 샘플링
            for (int i = 0; i < lineCount; i++)
            {
                var line = snapshot.GetLineFromLineNumber(i);
                var text = line.GetText();
                if (text.Length == 0) continue;
                if (text[0] == '\t') tabLines++;
                else if (text[0] == ' ' && text.Length >= 2) spaceLines++;
            }

            bool originalUsesTabs = tabLines > spaceLines;

            // LLM 코드의 들여쓰기 스타일 감지
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

            // 스타일이 같으면 변환 불필요
            if (originalUsesTabs == llmUsesTabs) return code;

            if (originalUsesTabs && !llmUsesTabs)
            {
                // LLM이 스페이스를 사용 → 탭으로 변환
                // 스페이스 단위 감지 (가장 짧은 들여쓰기 스페이스 수)
                int indentUnit = DetectSpaceIndentUnit(codeLines);
                if (indentUnit <= 0) return code;

                var sb = new System.Text.StringBuilder(code.Length);
                for (int i = 0; i < codeLines.Length; i++)
                {
                    if (i > 0) sb.Append("\r\n");
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
                // LLM이 탭을 사용 → 스페이스로 변환
                // 원본의 스페이스 단위 감지
                int indentUnit = 4; // 기본값
                var origLines = new string[Math.Min(snapshot.LineCount, 100)];
                for (int i = 0; i < origLines.Length; i++)
                    origLines[i] = snapshot.GetLineFromLineNumber(i).GetText();
                int detected = DetectSpaceIndentUnit(origLines);
                if (detected > 0) indentUnit = detected;

                var sb = new System.Text.StringBuilder(code.Length * 2);
                for (int i = 0; i < codeLines.Length; i++)
                {
                    if (i > 0) sb.Append("\r\n");
                    var line = codeLines[i];
                    int tabs = 0;
                    while (tabs < line.Length && line[tabs] == '\t') tabs++;
                    sb.Append(' ', tabs * indentUnit);
                    sb.Append(line, tabs, line.Length - tabs);
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// 스페이스 들여쓰기의 단위(2, 4, 8 등)를 감지한다.
        /// 줄 앞 스페이스 수의 최대공약수(GCD)로 판단한다.
        /// </summary>
        private static int DetectSpaceIndentUnit(string[] lines)
        {
            int gcd = 0;
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                int spaces = 0;
                while (spaces < line.Length && line[spaces] == ' ') spaces++;
                if (spaces < 2) continue; // 1칸은 의미 없음
                gcd = gcd == 0 ? spaces : Gcd(gcd, spaces);
            }
            return gcd > 0 ? gcd : 4; // 감지 실패 시 기본 4
        }

        private static int Gcd(int a, int b)
        {
            while (b != 0) { int t = b; b = a % b; a = t; }
            return a;
        }

        // ── 결과 표시 ──────────────────────────────────────────

        /// <summary>
        /// Markdown 문자열을 파싱하여 FlowDocument로 렌더링한다.
        /// 파싱 실패 시 플레인 텍스트로 폴백한다.
        /// </summary>
        private void ShowMarkdown(string markdown)
        {
            try
            {
                var (fg, bg) = GetThemeColors();
                _docViewer.Document = MarkdownToFlowDocument.Convert(markdown, fg, bg);
            }
            catch (Exception ex)
            {
                // Markdown 렌더링 실패 시 오류 정보와 함께 플레인 텍스트로 표시
                ShowPlainText("[Markdown 렌더링 오류: " + ex.GetType().Name + " - " + ex.Message + "]\n\n" + markdown);
            }
        }

        /// <summary>
        /// 플레인 텍스트를 VS 테마 색상이 적용된 FlowDocument로 표시한다.
        /// </summary>
        private void ShowPlainText(string text)
        {
            var (fg, bg) = GetThemeColors();
            var doc = new FlowDocument
            {
                FontFamily = new FontFamily("Malgun Gothic, Consolas, Segoe UI"),
                FontSize = 13,
                PagePadding = new Thickness(12),
                Foreground = new SolidColorBrush(fg),
                Background = new SolidColorBrush(bg)
            };
            doc.Blocks.Add(new Paragraph(new Run(text)));
            _docViewer.Document = doc;
        }

        // ── VS 테마 색상 획득 ──────────────────────────────────

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
                // VS 외부 실행 시 폴백 (Dark Theme 기본값)
                return (
                    Color.FromRgb(220, 220, 220),
                    Color.FromRgb(30, 30, 30)
                );
            }
        }

        // ── 유틸리티 ───────────────────────────────────────────

        private void SetBusy(bool busy)
        {
            _btnCurrentFile.IsEnabled = !busy;
            _btnSelection.IsEnabled = !busy;
            _btnRefresh.IsEnabled = !busy;
            _btnApply.IsEnabled = !busy && _lastResult != null;
            _cmbTool.IsEnabled = !busy;
            _txtServerUrl.IsEnabled = !busy;
        }
    }
}
