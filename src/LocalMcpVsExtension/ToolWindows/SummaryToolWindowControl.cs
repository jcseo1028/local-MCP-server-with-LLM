using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Community.VisualStudio.Toolkit;
using LocalMcpVsExtension.Services;
using Microsoft.VisualStudio.Shell;

namespace LocalMcpVsExtension.ToolWindows
{
    /// <summary>
    /// Local MCP 코드 요약 Tool Window 의 WPF 컨트롤.
    /// XAML 컴파일 이슈를 피하기 위해 프로그래밍 방식으로 UI를 구성한다.
    /// </summary>
    public sealed class SummaryToolWindowControl : UserControl
    {
        private readonly McpRestClient _client = new McpRestClient();

        // ── UI 요소 ────────────────────────────────────────────
        private readonly Button _btnSummarizeFile;
        private readonly Button _btnSummarizeSelection;
        private readonly TextBox _txtServerUrl;
        private readonly TextBox _txtResult;
        private readonly TextBlock _txtStatus;
        private readonly TextBlock _txtLanguage;

        public SummaryToolWindowControl()
        {
            // ── Grid 레이아웃 (3행) ─────────────────────────────
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── Row 0: 도구 모음 ────────────────────────────────
            var toolbar = new WrapPanel { Margin = new Thickness(6, 6, 6, 4) };

            _btnSummarizeFile = new Button
            {
                Content = "현재 파일 요약",
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 4, 4)
            };
            _btnSummarizeFile.Click += BtnSummarizeFile_Click;
            toolbar.Children.Add(_btnSummarizeFile);

            _btnSummarizeSelection = new Button
            {
                Content = "선택 영역 요약",
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 12, 4)
            };
            _btnSummarizeSelection.Click += BtnSummarizeSelection_Click;
            toolbar.Children.Add(_btnSummarizeSelection);

            toolbar.Children.Add(new TextBlock
            {
                Text = "서버:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 4)
            });

            _txtServerUrl = new TextBox
            {
                Text = "http://localhost:5100",
                Width = 200,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(2),
                Margin = new Thickness(0, 0, 0, 4)
            };
            toolbar.Children.Add(_txtServerUrl);

            Grid.SetRow(toolbar, 0);
            grid.Children.Add(toolbar);

            // ── Row 1: 결과 표시 영역 ──────────────────────────
            _txtResult = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                FontFamily = new FontFamily("Malgun Gothic, Consolas, Courier New"),
                FontSize = 13,
                Padding = new Thickness(8),
                BorderThickness = new Thickness(0, 1, 0, 1),
                Text = "도구 창이 열렸습니다.\n" +
                       "위 버튼을 클릭하여 코드 요약을 시작하세요.\n\n" +
                       "사전 조건:\n" +
                       "  1. Ollama 실행 중 (ollama serve)\n" +
                       "  2. MCP 서버 실행 중 (dotnet run)"
            };
            Grid.SetRow(_txtResult, 1);
            grid.Children.Add(_txtResult);

            // ── Row 2: 상태 표시줄 ─────────────────────────────
            var statusBar = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = SystemColors.ActiveBorderBrush,
                Padding = new Thickness(6, 4, 6, 4)
            };
            var statusPanel = new DockPanel();

            _txtStatus = new TextBlock { Text = "준비" };
            DockPanel.SetDock(_txtStatus, Dock.Left);
            statusPanel.Children.Add(_txtStatus);

            _txtLanguage = new TextBlock
            {
                Text = "",
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(_txtLanguage, Dock.Right);
            statusPanel.Children.Add(_txtLanguage);

            statusBar.Child = statusPanel;
            Grid.SetRow(statusBar, 2);
            grid.Children.Add(statusBar);

            Content = grid;
        }

        // ── 이벤트 핸들러 ──────────────────────────────────────

#pragma warning disable VSSDK007 // 이벤트 핸들러에서 fire-and-forget 허용
        private void BtnSummarizeFile_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await SummarizeAsync(selectionOnly: false);
            });
        }

        private void BtnSummarizeSelection_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await SummarizeAsync(selectionOnly: true);
            });
        }
#pragma warning restore VSSDK007

        // ── 핵심 로직 ──────────────────────────────────────────

        private async Task SummarizeAsync(bool selectionOnly)
        {
            try
            {
                SetBusy(true);
                _txtStatus.Text = "코드를 가져오는 중...";

                var docView = await VS.Documents.GetActiveDocumentViewAsync();
                if (docView?.TextBuffer == null)
                {
                    ShowMessage("열린 문서가 없습니다.");
                    return;
                }

                string code;
                if (selectionOnly)
                {
                    var selection = docView.TextView?.Selection;
                    if (selection == null || selection.SelectedSpans.Count == 0)
                    {
                        ShowMessage("선택된 텍스트가 없습니다.");
                        return;
                    }
                    code = string.Join(Environment.NewLine,
                        selection.SelectedSpans.Select(span => span.GetText()));
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        ShowMessage("선택된 텍스트가 없습니다.");
                        return;
                    }
                }
                else
                {
                    code = docView.TextBuffer.CurrentSnapshot.GetText();
                }

                if (string.IsNullOrWhiteSpace(code))
                {
                    ShowMessage("빈 문서입니다.");
                    return;
                }

                // 언어 감지
                var filePath = docView.FilePath;
                var language = LanguageDetector.FromFilePath(filePath);
                _txtLanguage.Text = "언어: " + language;

                // REST 호출
                var serverUrl = _txtServerUrl.Text.TrimEnd('/');
                _txtStatus.Text = "MCP 서버 요청 중...";
                _txtResult.Text = "요약을 생성하고 있습니다.\n" +
                                  "모델 크기에 따라 10~60초 소요될 수 있습니다...";

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await _client.CallToolAsync(
                    serverUrl, "summarize_current_code", code, language);
                sw.Stop();

                _txtResult.Text = result;
                _txtStatus.Text = string.Format(
                    "완료 ({0:F1}초) — {1:HH:mm:ss}", sw.Elapsed.TotalSeconds, DateTime.Now);
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                _txtResult.Text =
                    "MCP 서버에 연결할 수 없습니다.\n\n" +
                    "서버가 실행 중인지 확인하세요:\n" +
                    "  cd src/LocalMcpServer && dotnet run\n\n" +
                    "오류: " + ex.Message;
                _txtStatus.Text = "연결 실패";
            }
            catch (TaskCanceledException)
            {
                _txtResult.Text =
                    "요청 시간이 초과되었습니다.\n" +
                    "Ollama가 실행 중인지 확인하세요.";
                _txtStatus.Text = "시간 초과";
            }
            catch (Exception ex)
            {
                _txtResult.Text = "오류가 발생했습니다:\n" + ex.Message;
                _txtStatus.Text = "오류";
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ── 유틸리티 ───────────────────────────────────────────

        private void SetBusy(bool busy)
        {
            _btnSummarizeFile.IsEnabled = !busy;
            _btnSummarizeSelection.IsEnabled = !busy;
            _txtServerUrl.IsEnabled = !busy;
        }

        private void ShowMessage(string message)
        {
            _txtResult.Text = message;
            _txtStatus.Text = "오류";
        }
    }
}
