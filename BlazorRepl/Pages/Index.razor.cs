using Microsoft.AspNetCore.Components.Web;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting.Hosting;

namespace BlazorRepl.Pages
{
    public partial class Index
    {
    public bool Disabled { get; set; } = true;
    public string Output { get; set; } = "";
    public string Input { get; set; } = "";
    private CSharpCompilation _previousCompilation;
    private IEnumerable<MetadataReference> _references;
    private object[] _submissionStates = { null, null };
    private int _submissionIndex;
    private List<string> _history = new List<string>();
    private int _historyIndex;
    bool _skipRender;

    [Inject]
    NavigationManager NavigationManager { get; set; }

    protected override async Task OnInitializedAsync()
    {
        var refs = AppDomain.CurrentDomain.GetAssemblies();
        var client = new HttpClient
        {
            BaseAddress = new Uri(NavigationManager.BaseUri)
        };

        var references = new List<MetadataReference>();

        foreach (var reference in refs.Where(x => !x.IsDynamic && !string.IsNullOrWhiteSpace(x.Location)))
        {
            var stream = await client.GetStreamAsync($"_framework/_bin/{reference.Location}");
            references.Add(MetadataReference.CreateFromStream(stream));
        }
        Disabled = false;
        _references = references;
    }

    protected override bool ShouldRender()
    {
        if (!_skipRender) return base.ShouldRender();
        _skipRender = false;
        return false;
    }

    public async Task OnKeyDown(KeyboardEventArgs e)
    {
        _skipRender = true;
        switch (e.Key)
        {
            case "ArrowUp" when _historyIndex > 0:
                _historyIndex--;
                Input = _history[_historyIndex];
                _skipRender = false;
                break;
            case "ArrowDown" when _historyIndex + 1 < _history.Count:
                _historyIndex++;
                Input = _history[_historyIndex];
                _skipRender = false;
                break;
            case "Escape":
                Input = "";
                _historyIndex = _history.Count;
                _skipRender = false;
                break;
            case "Enter":
                _skipRender = false;
                await Run();
                break;
        }
    }

    public async Task Run()
    {
        var code = Input;
        if (!string.IsNullOrEmpty(code))
        {
            _history.Add(code);
        }
        _historyIndex = _history.Count;
        Input = "";

        await RunSubmission(code);
    }

    private async Task RunSubmission(string code)
    {
        Output += $@"<br /><span class=""info"">{HttpUtility.HtmlEncode(code)}</span>";

        var previousOut = Console.Out;
        try
        {
            if (TryCompile(code, out var script, out var errorDiagnostics))
            {
                var writer = new StringWriter();
                Console.SetOut(writer);

                var entryPoint = _previousCompilation.GetEntryPoint(CancellationToken.None);
                var type = script.GetType($"{entryPoint.ContainingNamespace.MetadataName}.{entryPoint.ContainingType.MetadataName}");
                var entryPointMethod = type.GetMethod(entryPoint.MetadataName);

                var submission = (Func<object[], Task>)entryPointMethod.CreateDelegate(typeof(Func<object[], Task>));

                if (_submissionIndex >= _submissionStates.Length)
                {
                    Array.Resize(ref _submissionStates, Math.Max(_submissionIndex, _submissionStates.Length * 2));
                }

                var returnValue = await ((Task<object>)submission(_submissionStates));
                if (returnValue != null)
                {
                    Console.WriteLine(CSharpObjectFormatter.Instance.FormatObject(returnValue));
                }

                var output = HttpUtility.HtmlEncode(writer.ToString());
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Output += $"<br />{output}";
                }
            }
            else
            {
                foreach (var diag in errorDiagnostics)
                {
                    Output += $@"<br / ><span class=""error"">{HttpUtility.HtmlEncode(diag)}</span>";
                }
            }
        }
        catch (Exception ex)
        {
            Output += $@"<br /><span class=""error"">{HttpUtility.HtmlEncode(CSharpObjectFormatter.Instance.FormatException(ex))}</span>";
        }
        finally
        {
            Console.SetOut(previousOut);
        }
    }

    private bool TryCompile(string source, out Assembly assembly, out IEnumerable<Diagnostic> errorDiagnostics)
    {
        assembly = null;
        var scriptCompilation = CSharpCompilation.CreateScriptCompilation(
            Path.GetRandomFileName(),
            CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithKind(SourceCodeKind.Script).WithLanguageVersion(LanguageVersion.Preview)),
            _references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, usings: new[]
            {
                    "System",
                    "System.IO",
                    "System.Collections.Generic",
                    "System.Console",
                    "System.Diagnostics",
                    "System.Dynamic",
                    "System.Linq",
                    "System.Linq.Expressions",
                    "System.Net.Http",
                    "System.Text",
                    "System.Threading.Tasks"
                    }),
            _previousCompilation
        );

        errorDiagnostics = scriptCompilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error);
        if (errorDiagnostics.Any())
        {
            return false;
        }

        using var peStream = new MemoryStream();
        var emitResult = scriptCompilation.Emit(peStream);

        if (!emitResult.Success) return false;
        _submissionIndex++;
        _previousCompilation = scriptCompilation;
        assembly = Assembly.Load(peStream.ToArray());
        return true;
    }
    }
}
