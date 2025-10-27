// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using CommunityToolkit.WinUI.Controls.Renderers;
using CommunityToolkit.WinUI.Controls.Renderers.ObjectRenderers;
using CommunityToolkit.WinUI.Controls.Renderers.ObjectRenderers.Extensions;
using CommunityToolkit.WinUI.Controls.Renderers.ObjectRenderers.Inlines;
using CommunityToolkit.WinUI.Controls.TextElements;
using Markdig;
using Markdig.Syntax;

namespace CommunityToolkit.WinUI.Controls;

[TemplatePart(Name = MarkdownContainerName, Type = typeof(Grid))]
public partial class MarkdownTextBlock : Control
{
    private const string MarkdownContainerName = "MarkdownContainer";
    private Grid? _container;
    private MarkdownPipeline _pipeline = null!;
    private MyFlowDocument _document;
    private WinUIRenderer? _renderer;

    // A small debounce delay to coalesce very fast updates (still available if you want to use it)
    public TimeSpan UpdateDebounceDelayMs = TimeSpan.FromMilliseconds(200);

    // Coalescing state: latest requested text and processing flag
    private readonly object _updateLock = new();
    private string? _latestText;
    private bool _isProcessing;

    public event EventHandler<LinkClickedEventArgs>? OnLinkClicked;

    internal bool RaiseLinkClickedEvent(Uri uri)
    {
        if (OnLinkClicked == null)
        {
            return false;
        }
        var args = new LinkClickedEventArgs(uri);
        OnLinkClicked?.Invoke(this, args);
        return args.Handled;
    }

    private static void OnConfigChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownTextBlock self && e.NewValue != null)
        {
            self.ApplyConfig(self.Config);
        }
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownTextBlock self && e.NewValue != null)
        {
            // Fire-and-forget: publish request and let the coalescing loop handle sequencing
            self.QueueUpdateText();
        }
    }

    public MarkdownTextBlock()
    {
        this.DefaultStyleKey = typeof(MarkdownTextBlock);
        _document = new MyFlowDocument();
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        var pipelineBuilder = new MarkdownPipelineBuilder();

        // NOTE: Order matters here
        if (UseEmphasisExtras) pipelineBuilder = pipelineBuilder.UseEmphasisExtras();
        if (UsePipeTables) pipelineBuilder = pipelineBuilder.UsePipeTables();
        if (UseListExtras) pipelineBuilder = pipelineBuilder.UseListExtras();
        if (UseTaskLists) pipelineBuilder = pipelineBuilder.UseTaskLists();
        if (UseAutoLinks) pipelineBuilder = pipelineBuilder.UseAutoLinks();
        if (UseSoftlineBreakAsHardlineBreak) pipelineBuilder = pipelineBuilder.UseSoftlineBreakAsHardlineBreak();
        if (DisableHtml) pipelineBuilder = pipelineBuilder.DisableHtml();

        _pipeline = pipelineBuilder.Build();

        _container = (Grid)GetTemplateChild(MarkdownContainerName);
        _container.Children.Clear();
        _container.Children.Add(_document.RichTextBlock);
        Build();
    }

    private void ApplyConfig(MarkdownConfig config)
    {
        _renderer?.Config = config;
    }

    private void ApplyText(bool rerender)
    {
        if (_renderer != null)
        {
            if (rerender)
            {
                _renderer.ReloadDocument();
            }

            if (!string.IsNullOrEmpty(Text))
            {
                var parsedMarkdown = Markdown.Parse(Text, _pipeline);
                this.MarkdownDocument = parsedMarkdown;
                _renderer.Render(parsedMarkdown);
            }
        }
    }

    private void Build()
    {
        if (Config != null)
        {
            if (_renderer == null)
            {
                _renderer = new WinUIRenderer(_document, Config, this);

                // Default block renderers
                _renderer.ObjectRenderers.Add(new CodeBlockRenderer());
                _renderer.ObjectRenderers.Add(new ListRenderer());
                _renderer.ObjectRenderers.Add(new ListItemRenderer());
                _renderer.ObjectRenderers.Add(new HeadingRenderer());
                _renderer.ObjectRenderers.Add(new ParagraphRenderer());
                _renderer.ObjectRenderers.Add(new QuoteBlockRenderer());
                _renderer.ObjectRenderers.Add(new ThematicBreakRenderer());
                if (!DisableHtml) _renderer.ObjectRenderers.Add(new HtmlBlockRenderer());

                // Default inline renderers
                if (UseAutoLinks) _renderer.ObjectRenderers.Add(new AutoLinkInlineRenderer());
                _renderer.ObjectRenderers.Add(new CodeInlineRenderer());
                _renderer.ObjectRenderers.Add(new DelimiterInlineRenderer());
                _renderer.ObjectRenderers.Add(new EmphasisInlineRenderer());
                if (!DisableHtml) _renderer.ObjectRenderers.Add(new HtmlEntityInlineRenderer());
                _renderer.ObjectRenderers.Add(new LineBreakInlineRenderer());
                if (!DisableLinks) _renderer.ObjectRenderers.Add(new LinkInlineRenderer());
                _renderer.ObjectRenderers.Add(new LiteralInlineRenderer());
                if (!DisableLinks) _renderer.ObjectRenderers.Add(new ContainerInlineRenderer());

                // Extension renderers
                if (UsePipeTables) _renderer.ObjectRenderers.Add(new TableRenderer());
                if (UseTaskLists) _renderer.ObjectRenderers.Add(new TaskListRenderer());
                if (!DisableHtml) _renderer.ObjectRenderers.Add(new HtmlInlineRenderer());
            }
            _pipeline.Setup(_renderer);
            ApplyText(false);
        }
    }

    /// <summary>
    /// Publish latest text and start a single, coalescing processing loop if not already running.
    /// The loop processes the first snapshot immediately and, after finishing, processes the latest snapshot
    /// produced while the previous was running. Intermediate updates are coalesced to the latest.
    /// This matches: A processed immediately; B/C ignored while A runs; then C processed; etc.
    /// </summary>
    private void QueueUpdateText()
    {
        lock (_updateLock)
        {
            _latestText = Text;
            if (_isProcessing)
            {
                // already processing — the loop will pick up the new latest value when it finishes current iteration
                return;
            }
            _isProcessing = true;
        }

        // Fire-and-forget the processing loop
        _ = ProcessLoopAsync();
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            var firstIteration = true;

            while (true)
            {
                string snapshot;
                lock (_updateLock)
                {
                    snapshot = _latestText ?? string.Empty;
                    _latestText = null; // claim it
                }

                // --- Debounce: skip on the very first iteration, apply on subsequent iterations ---
                if (!firstIteration && UpdateDebounceDelayMs > TimeSpan.Zero)
                {
                    // Wait without capturing the synchronization context
                    await Task.Delay(UpdateDebounceDelayMs).ConfigureAwait(false);

                    // If newer text arrived during the delay, use the latest instead
                    lock (_updateLock)
                    {
                        if (_latestText != null)
                        {
                            snapshot = _latestText;
                            _latestText = null; // claim latest
                        }
                    }
                }
                // mark that we've done the first iteration (so future loops debounce)
                firstIteration = false;
                // -------------------------------------------------------------------------------

                // If renderer/pipeline not ready, apply synchronously on UI thread and continue
                if (_renderer == null || _pipeline == null)
                {
                    DispatcherQueue?.TryEnqueue(() => ApplyText(true));
                }
                else
                {
                    // Parse on background thread
                    MarkdownDocument parsedMarkdown;
                    try
                    {
                        parsedMarkdown = await Task.Run(() => Markdown.Parse(snapshot, _pipeline)).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // If parsing fails, swallow and continue to next snapshot (do not stop loop)
                        parsedMarkdown = null!;
                    }

                    // Render on UI thread and wait till finished
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        try
                        {
                            if (parsedMarkdown != null)
                            {
                                MarkdownDocument = parsedMarkdown;
                                _renderer.ReloadDocument();
                                _renderer.Render(parsedMarkdown);
                            }
                            tcs.SetResult(true);
                        }
                        catch (Exception ex)
                        {
                            tcs.SetException(ex);
                        }
                    });

                    // If DispatcherQueue was null and tcs never set, guard with a completed task
                    if (tcs.Task.Status == TaskStatus.Created)
                    {
                        // No UI dispatch available; continue without waiting (best-effort)
                        await Task.CompletedTask;
                    }
                    else
                    {
                        await tcs.Task.ConfigureAwait(false);
                    }
                }

                // Let the runtime schedule any incoming QueueUpdateText calls
                await Task.Yield();

                // If no newer snapshot arrived while we were working, exit loop
                lock (_updateLock)
                {
                    if (_latestText == null)
                    {
                        _isProcessing = false;
                        break;
                    }
                    // else continue and process the latestText on next iteration
                }
            }
        }
        catch (Exception)
        {
            // Ensure processing flag cleared on unexpected error
            lock (_updateLock)
            {
                _isProcessing = false;
            }
        }
    }
}
