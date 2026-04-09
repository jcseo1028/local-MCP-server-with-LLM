using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LocalMcpVsExtension.Services
{
    /// <summary>
    /// Markdig AST를 WPF FlowDocument로 변환한다.
    /// VS 테마 색상(Dark/Light)을 반영하여 스타일을 적용한다.
    /// </summary>
    internal static class MarkdownToFlowDocument
    {
        private static readonly FontFamily MonoFont = new FontFamily("Consolas, Courier New, monospace");
        private static readonly FontFamily SansFont = new FontFamily("Malgun Gothic, Segoe UI, sans-serif");

        /// <summary>
        /// Markdown 문자열을 VS 테마에 맞는 FlowDocument로 변환한다.
        /// </summary>
        public static FlowDocument Convert(string markdown, Color foreground, Color background)
        {
            bool isDark = IsDark(background);
            var ctx = new RenderContext(foreground, isDark);

            var doc = new FlowDocument
            {
                FontFamily = SansFont,
                FontSize = 13,
                PagePadding = new Thickness(12),
                Foreground = new SolidColorBrush(foreground),
                Background = new SolidColorBrush(background)
            };

            var pipeline = new MarkdownPipelineBuilder().Build();
            var mdDoc = Markdown.Parse(markdown, pipeline);

            foreach (var block in mdDoc)
                AddBlock(doc.Blocks, block, ctx);

            return doc;
        }

        // ── 테마 판별 ──────────────────────────────────────────

        private static bool IsDark(Color c)
        {
            double brightness = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            return brightness < 0.5;
        }

        // ── 렌더 컨텍스트 ──────────────────────────────────────

        private sealed class RenderContext
        {
            public SolidColorBrush Foreground { get; }
            public SolidColorBrush CodeBlockBg { get; }
            public SolidColorBrush CodeInlineBg { get; }
            public SolidColorBrush HeadingFg { get; }
            public SolidColorBrush HrBrush { get; }
            public SolidColorBrush LinkFg { get; }
            public SolidColorBrush QuoteBorder { get; }

            public RenderContext(Color fg, bool isDark)
            {
                Foreground = new SolidColorBrush(fg);

                CodeBlockBg = new SolidColorBrush(isDark
                    ? Color.FromRgb(42, 42, 46)
                    : Color.FromRgb(243, 243, 243));

                CodeInlineBg = new SolidColorBrush(isDark
                    ? Color.FromRgb(55, 55, 60)
                    : Color.FromRgb(230, 230, 230));

                HeadingFg = new SolidColorBrush(isDark
                    ? Color.FromRgb(86, 156, 214)   // VS blue (dark)
                    : Color.FromRgb(0, 102, 153));   // dark blue (light)

                HrBrush = new SolidColorBrush(isDark
                    ? Color.FromRgb(80, 80, 80)
                    : Color.FromRgb(200, 200, 200));

                LinkFg = new SolidColorBrush(isDark
                    ? Color.FromRgb(78, 201, 176)    // VS teal
                    : Color.FromRgb(0, 122, 204));   // VS blue

                QuoteBorder = new SolidColorBrush(isDark
                    ? Color.FromRgb(80, 80, 90)
                    : Color.FromRgb(180, 180, 190));
            }
        }

        // ── 블록 변환 ──────────────────────────────────────────

        private static void AddBlock(BlockCollection blocks, Markdig.Syntax.Block block, RenderContext ctx)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    blocks.Add(CreateHeading(heading, ctx));
                    break;

                case ParagraphBlock paragraph:
                    blocks.Add(CreateParagraph(paragraph, ctx));
                    break;

                case ListBlock list:
                    blocks.Add(CreateList(list, ctx));
                    break;

                case FencedCodeBlock fenced:
                    blocks.Add(CreateCodeBlock(fenced, ctx));
                    break;

                case CodeBlock code:
                    blocks.Add(CreateCodeBlock(code, ctx));
                    break;

                case ThematicBreakBlock _:
                    blocks.Add(CreateHorizontalRule(ctx));
                    break;

                case QuoteBlock quote:
                    blocks.Add(CreateQuote(quote, ctx));
                    break;

                // 기타 ContainerBlock (예: 중첩 구조)
                case ContainerBlock container:
                    foreach (var child in container)
                        AddBlock(blocks, child, ctx);
                    break;
            }
        }

        private static Paragraph CreateHeading(HeadingBlock heading, RenderContext ctx)
        {
            double fontSize;
            switch (heading.Level)
            {
                case 1: fontSize = 22; break;
                case 2: fontSize = 18; break;
                case 3: fontSize = 15; break;
                default: fontSize = 14; break;
            }

            var p = new Paragraph
            {
                Foreground = ctx.HeadingFg,
                FontWeight = FontWeights.Bold,
                FontSize = fontSize,
                Margin = new Thickness(0, 12, 0, 4)
            };

            if (heading.Inline != null)
                AddInlines(p.Inlines, heading.Inline, ctx);

            return p;
        }

        private static Paragraph CreateParagraph(ParagraphBlock para, RenderContext ctx)
        {
            var p = new Paragraph
            {
                Margin = new Thickness(0, 2, 0, 6),
                Foreground = ctx.Foreground
            };

            if (para.Inline != null)
                AddInlines(p.Inlines, para.Inline, ctx);

            return p;
        }

        private static System.Windows.Documents.List CreateList(ListBlock list, RenderContext ctx)
        {
            var wpfList = new System.Windows.Documents.List
            {
                MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Margin = new Thickness(0, 2, 0, 6),
                Padding = new Thickness(20, 0, 0, 0)
            };

            foreach (var item in list)
            {
                if (item is ListItemBlock liBlock)
                {
                    var li = new ListItem();
                    foreach (var child in liBlock)
                        AddBlock(li.Blocks, child, ctx);
                    wpfList.ListItems.Add(li);
                }
            }

            return wpfList;
        }

        private static Paragraph CreateCodeBlock(LeafBlock codeBlock, RenderContext ctx)
        {
            string text = codeBlock.Lines.ToString().TrimEnd();

            return new Paragraph(new Run(text)
            {
                FontFamily = MonoFont,
                FontSize = 12
            })
            {
                Background = ctx.CodeBlockBg,
                Foreground = ctx.Foreground,
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 4, 0, 8)
            };
        }

        private static Paragraph CreateHorizontalRule(RenderContext ctx)
        {
            return new Paragraph
            {
                Margin = new Thickness(0, 8, 0, 8),
                BorderBrush = ctx.HrBrush,
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
        }

        private static Section CreateQuote(QuoteBlock quote, RenderContext ctx)
        {
            var section = new Section
            {
                BorderBrush = ctx.QuoteBorder,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(12, 4, 4, 4),
                Margin = new Thickness(0, 4, 0, 8)
            };

            foreach (var child in quote)
                AddBlock(section.Blocks, child, ctx);

            return section;
        }

        // ── 인라인 변환 ────────────────────────────────────────

        private static void AddInlines(InlineCollection inlines, ContainerInline container, RenderContext ctx)
        {
            foreach (var inline in container)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        inlines.Add(new Run(literal.Content.ToString()));
                        break;

                    case EmphasisInline emphasis:
                    {
                        Span span;
                        if (emphasis.DelimiterCount >= 2)
                            span = new Bold();
                        else
                            span = new Italic();

                        AddInlines(span.Inlines, emphasis, ctx);
                        inlines.Add(span);
                        break;
                    }

                    case CodeInline code:
                        inlines.Add(new Run(code.Content)
                        {
                            FontFamily = MonoFont,
                            FontSize = 12,
                            Background = ctx.CodeInlineBg
                        });
                        break;

                    case LineBreakInline _:
                        inlines.Add(new LineBreak());
                        break;

                    case LinkInline link:
                    {
                        var linkSpan = new Span
                        {
                            Foreground = ctx.LinkFg,
                            TextDecorations = TextDecorations.Underline
                        };
                        AddInlines(linkSpan.Inlines, link, ctx);
                        inlines.Add(linkSpan);
                        break;
                    }

                    case ContainerInline nested:
                        AddInlines(inlines, nested, ctx);
                        break;

                    default:
                    {
                        string? text = inline.ToString();
                        if (!string.IsNullOrEmpty(text))
                            inlines.Add(new Run(text));
                        break;
                    }
                }
            }
        }
    }
}
