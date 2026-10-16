using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AI.Text;
using Windows.Storage;

namespace WindowsAIStructuredRepro;

public partial class App : Application
{
    private const string MinimalSchema = """
        {"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}
        """;

    private const string DefaultSchema = """
        {
          "type": "object",
          "properties": {
            "summary": {
              "description": "A brief, accurate answer to the user's request. Do not invent details not supported by the prompt.",
              "type": "string"
            },
            "keyPoints": {
              "description": "Specific points supporting the summary. Use an empty list when there are no distinct supporting points.",
              "type": "array",
              "items": { "type": "string" }
            },
            "category": {
              "description": "A short, broad category for the response, such as General, Technical, or Creative.",
              "type": "string"
            },
            "sentiment": {
              "description": "Classify the overall tone. Use Neutral for objective or factual replies.",
              "type": "string",
              "enum": ["Neutral", "Positive", "Negative", "Mixed"]
            }
          },
          "additionalProperties": false,
          "required": ["summary", "keyPoints", "category", "sentiment"]
        }
        """;

    private Window _window = null!;
    private readonly TextBox _prompt = new()
    {
        Text = "who is the fastest gun in the west?",
        Header = "Prompt",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBox _schema = new()
    {
        Header = "JSON Schema (passed directly to the native API)",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 230,
        Text = DefaultSchema
    };
    private readonly CheckBox _prefix = new() { Content = "Prefix prompt with 'User: '", IsChecked = true };
    private readonly TextBlock _status = new() { Text = "Run one request per fresh process for a clean comparison.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _eventsView = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 200 };
    private readonly Button _plain = new() { Content = "Plain text" };
    private readonly Button _structured = new() { Content = "Structured JSON" };
    private readonly Button _cancel = new() { Content = "Cancel wait", IsEnabled = false };
    private CancellationTokenSource? _cancellation;
    private Action? _cancelNative;
    private bool _running;
    private bool _abandoned;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new Window { Title = "Windows AI - raw WinUI repro" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_plain);
        actions.Children.Add(_structured);
        actions.Children.Add(_cancel);
        var root = new StackPanel { Padding = new Thickness(20), Spacing = 10 };
        root.Children.Add(_prompt);
        root.Children.Add(_prefix);
        root.Children.Add(_schema);
        root.Children.Add(actions);
        root.Children.Add(new TextBlock { Text = "Wait bound: 25 seconds. A timed-out native operation is not treated as success." });
        root.Children.Add(_status);
        root.Children.Add(_eventsView);
        _window.Content = new ScrollViewer { Content = root };
        _plain.Click += async (_, _) => await RunAsync(false, "manual-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"), false);
        _structured.Click += async (_, _) => await RunAsync(true, "manual-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"), false);
        _cancel.Click += (_, _) => _cancellation?.Cancel();
        _window.Activate();

        var activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
        var launchArguments = activation.Data is Windows.ApplicationModel.Activation.LaunchActivatedEventArgs launch
            ? launch.Arguments
            : args.Arguments;
        var tokens = launchArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Contains("--auto"))
        {
            _prefix.IsChecked = !tokens.Contains("--raw");
            if (tokens.Contains("--minimal"))
                _schema.Text = MinimalSchema;
            var label = tokens.FirstOrDefault(x => x.StartsWith("--label=", StringComparison.Ordinal))?[8..]
                ?? "automatic-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var structured = !tokens.Contains("--plain");
            _window.DispatcherQueue.TryEnqueue(async () => await RunAsync(structured, label, tokens.Contains("--exit")));
        }
    }

    private async Task RunAsync(bool structured, string label, bool exitAfter)
    {
        if (_running || _abandoned)
            return;
        if (label.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || label.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("The result label must be a simple file name.", nameof(label));

        _running = true;
        _plain.IsEnabled = _structured.IsEnabled = false;
        _cancel.IsEnabled = true;
        var prompt = (_prefix.IsChecked == true ? "User: " : "") + _prompt.Text;
        var schema = _schema.Text;
        var clock = Stopwatch.StartNew();
        var events = new ConcurrentQueue<string>();
        var progressCount = 0;
        var resultText = "";
        var outcome = "pending";
        var status = "";
        void Log(string text)
        {
            var line = $"{clock.Elapsed.TotalSeconds:F3}s {text}";
            events.Enqueue(line);
            _window.DispatcherQueue.TryEnqueue(() => _eventsView.Text += line + Environment.NewLine);
        }

        _eventsView.Text = "";
        _status.Text = "Calling the native Windows AI API...";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        _cancellation = cancellation;
        using var registration = cancellation.Token.Register(() => Volatile.Read(ref _cancelNative)?.Invoke());
        var task = Task.Run(async () =>
        {
            Log($"GetReadyState={LanguageModel.GetReadyState()}");
            Log("LanguageModel.CreateAsync ENTER");
            using var model = await LanguageModel.CreateAsync();
            Log("LanguageModel.CreateAsync RETURN");
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                if (module.ModuleName is "Microsoft.Windows.AI.Text.dll" or "Microsoft.UI.Xaml.dll" or "Microsoft.WindowsAppRuntime.dll")
                {
                    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(module.FileName)));
                    Log($"MODULE {module.ModuleName} version={module.FileVersionInfo.FileVersion} SHA256={hash}");
                }
            }
            cancellation.Token.ThrowIfCancellationRequested();

            if (structured)
            {
                Log("GenerateStructuredJsonResponseAsync ENTER");
                var operation = model.GenerateStructuredJsonResponseAsync(prompt, schema, new LanguageModelOptions());
                Log("GenerateStructuredJsonResponseAsync RETURNED ASYNC OPERATION");
                Volatile.Write(ref _cancelNative, operation.Cancel);
                if (cancellation.IsCancellationRequested)
                    operation.Cancel();
                operation.Progress = (_, text) =>
                {
                    Interlocked.Increment(ref progressCount);
                    Log($"PROGRESS {text.Length} characters");
                };
                var result = await operation;
                status = result.Status.ToString();
                resultText = result.Text;
                Log($"RESULT status={status} error={result.ExtendedError}");
                if (result.Status != GenerateStructuredJsonResponseStatus.Complete)
                    throw new InvalidOperationException($"Native structured result: {result.Status}", result.ExtendedError);
            }
            else
            {
                Log("CreateContext ENTER");
                using var context = model.CreateContext();
                Log("CreateContext RETURN");
                Log("GenerateResponseAsync ENTER");
                var operation = model.GenerateResponseAsync(context, prompt, new LanguageModelOptions());
                Log("GenerateResponseAsync RETURNED ASYNC OPERATION");
                Volatile.Write(ref _cancelNative, operation.Cancel);
                if (cancellation.IsCancellationRequested)
                    operation.Cancel();
                operation.Progress = (_, text) =>
                {
                    Interlocked.Increment(ref progressCount);
                    Log($"PROGRESS {text.Length} characters");
                };
                var result = await operation;
                status = result.Status.ToString();
                resultText = result.Text;
                Log($"RESULT status={status} error={result.ExtendedError}");
                if (result.Status != LanguageModelResponseStatus.Complete)
                    throw new InvalidOperationException($"Native text result: {result.Status}", result.ExtendedError);
            }
        });
        try
        {
            await task.WaitAsync(cancellation.Token);
            outcome = "complete";
            Log($"TEXT {resultText}");
            _status.Text = "Native response completed.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            outcome = "cancelled-or-25s-bound";
            _abandoned = !task.IsCompleted;
            Log($"WAIT CANCELLED; native worker complete={task.IsCompleted}. This does not prove native recovery.");
            _status.Text = "No completion within the wait bound, or wait cancelled. Restart the app before another trial.";
            _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception error)
        {
            outcome = "error";
            Log(error.ToString());
            _status.Text = error.Message;
        }
        finally
        {
            _cancellation = null;
            Volatile.Write(ref _cancelNative, null);
            _running = false;
            _cancel.IsEnabled = false;
            _plain.IsEnabled = _structured.IsEnabled = !_abandoned;
            var folder = Path.Combine(ApplicationData.Current.LocalFolder.Path, "Results");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "result-" + label + ".json");
            var report = new
            {
                label,
                utc = DateTime.UtcNow,
                os = Environment.OSVersion.VersionString,
                processId = Environment.ProcessId,
                structured,
                prompt,
                schema = structured ? schema : null,
                outcome,
                nativeStatus = status,
                elapsedSeconds = clock.Elapsed.TotalSeconds,
                progressCount,
                text = resultText,
                events = events.ToArray()
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            _status.Text += Environment.NewLine + path;
            if (exitAfter)
                Exit();
        }
    }
}
