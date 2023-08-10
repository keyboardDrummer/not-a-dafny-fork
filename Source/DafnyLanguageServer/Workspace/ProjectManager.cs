using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntervalTree;
using Microsoft.Boogie;
using Microsoft.Dafny.LanguageServer.Language;
using Microsoft.Dafny.LanguageServer.Workspace.ChangeProcessors;
using Microsoft.Dafny.LanguageServer.Workspace.Notifications;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Microsoft.Dafny.LanguageServer.Workspace;

public delegate ProjectManager CreateProjectManager(
  ExecutionEngine boogieEngine,
  DafnyProject project);

public record FilePosition(Uri Uri, Position Position);

/// <summary>
/// Handles operation on a single document.
/// Handles migration of previously published document state
/// </summary>
public class ProjectManager : IDisposable {
  private readonly IRelocator relocator;
  public DafnyProject Project { get; }

  private readonly IdeStateObserver observer;
  public CompilationManager CompilationManager { get; private set; }
  private IDisposable observerSubscription;
  private readonly INotificationPublisher notificationPublisher;
  private readonly IVerificationProgressReporter verificationProgressReporter;
  private readonly ILogger<ProjectManager> logger;

  /// <summary>
  /// The version of this project.
  /// Is incremented when any file in the project is updated.
  /// Is used as part of project-wide notifications.
  /// Can be used by the client to ignore outdated notifications
  /// </summary>
  private int version;

  private int openFileCount;

  private VerifyOnMode AutomaticVerificationMode => options.Get(ServerCommand.Verification);

  private bool VerifyOnSave => options.Get(ServerCommand.Verification) == VerifyOnMode.Save;
  public List<FilePosition> ChangedVerifiables { get; set; } = new();
  public List<Location> RecentChanges { get; set; } = new();

  private readonly SemaphoreSlim workCompletedForCurrentVersion = new(1);
  private readonly DafnyOptions options;
  private readonly DafnyOptions serverOptions;
  private readonly CreateCompilationManager createCompilationManager;
  private readonly ExecutionEngine boogieEngine;
  private readonly IFileSystem fileSystem;

  public ProjectManager(
    DafnyOptions serverOptions,
    ILogger<ProjectManager> logger,
    IRelocator relocator,
    IFileSystem fileSystem,
    INotificationPublisher notificationPublisher,
    IVerificationProgressReporter verificationProgressReporter,
    CreateCompilationManager createCompilationManager,
    CreateIdeStateObserver createIdeStateObserver,
    ExecutionEngine boogieEngine,
    DafnyProject project) {
    Project = project;
    this.verificationProgressReporter = verificationProgressReporter;
    this.notificationPublisher = notificationPublisher;
    this.serverOptions = serverOptions;
    this.fileSystem = fileSystem;
    this.createCompilationManager = createCompilationManager;
    this.relocator = relocator;
    this.logger = logger;
    this.boogieEngine = boogieEngine;

    options = DetermineProjectOptions(project, serverOptions);
    options.Printer = new OutputLogger(logger);

    var initialCompilation = CreateInitialCompilation();
    observer = createIdeStateObserver(initialCompilation);
    CompilationManager = createCompilationManager(
        options, boogieEngine, initialCompilation, ImmutableDictionary<Uri, VerificationTree>.Empty
    );

    observerSubscription = Disposable.Empty;
  }

  private Compilation CreateInitialCompilation() {
    var rootUris = Project.GetRootSourceUris(fileSystem).Concat(options.CliRootSourceUris).ToList();
    return new Compilation(version, Project, rootUris);
  }

  private const int MaxRememberedChanges = 100;
  private const int MaxRememberedChangedVerifiables = 5;

  public void UpdateDocument(DidChangeTextDocumentParams documentChange) {
    observer.Migrate(documentChange, version + 1);
    var lastPublishedState = observer.LastPublishedState;
    var migratedVerificationTrees = lastPublishedState.VerificationTrees;

    lock (RecentChanges) {
      var newChanges = documentChange.ContentChanges.Where(c => c.Range != null).
        Select(contentChange => new Location {
          Range = contentChange.Range!,
          Uri = documentChange.TextDocument.Uri
        });
      var migratedChanges = RecentChanges.Select(location => {
        var newRange = relocator.RelocateRange(location.Range, documentChange, CancellationToken.None);
        if (newRange == null) {
          return null;
        }
        return new Location {
          Range = newRange,
          Uri = location.Uri
        };
      }).Where(r => r != null);
      RecentChanges = newChanges.Concat(migratedChanges).Take(MaxRememberedChanges).ToList()!;
    }

    StartNewCompilation(migratedVerificationTrees, lastPublishedState);
    TriggerVerificationForFile(documentChange.TextDocument.Uri.ToUri());
  }

  private void StartNewCompilation(IReadOnlyDictionary<Uri, VerificationTree> migratedVerificationTrees,
    IdeState lastPublishedState) {
    version++;
    logger.LogDebug("Clearing result for workCompletedForCurrentVersion");

    CompilationManager.CancelPendingUpdates();
    CompilationManager = createCompilationManager(
      options,
      boogieEngine,
      CreateInitialCompilation(),
      migratedVerificationTrees);

    observerSubscription.Dispose();
    var migratedUpdates = CompilationManager.CompilationUpdates.Select(document =>
      document.ToIdeState(lastPublishedState));
    observerSubscription = migratedUpdates.Subscribe(observer);

    CompilationManager.Start();
  }

  public void TriggerVerificationForFile(Uri triggeringFile) {
    if (AutomaticVerificationMode is VerifyOnMode.ChangeFile or VerifyOnMode.ChangeProject) {
      var _ = VerifyEverythingAsync(AutomaticVerificationMode == VerifyOnMode.ChangeFile ? triggeringFile : null);
    } else {
      logger.LogDebug("Setting result for workCompletedForCurrentVersion");
    }
  }

  private static DafnyOptions DetermineProjectOptions(DafnyProject projectOptions, DafnyOptions serverOptions) {
    var result = new DafnyOptions(serverOptions);

    foreach (var option in ServerCommand.Instance.Options) {
      var hasProjectFileValue = projectOptions.TryGetValue(option, TextWriter.Null, out var projectFileValue);
      if (hasProjectFileValue) {
        result.Options.OptionArguments[option] = projectFileValue;
        result.ApplyBinding(option);
      }
    }

    return result;
  }

  public void Save(TextDocumentIdentifier documentId) {
    if (VerifyOnSave) {
      logger.LogDebug("Clearing result for workCompletedForCurrentVersion");
      _ = VerifyEverythingAsync(documentId.Uri.ToUri());
    }
  }

  /// <summary>
  /// Needs to be thread-safe
  /// </summary>
  /// <returns></returns>
  public bool CloseDocument(out Task close) {
    if (Interlocked.Decrement(ref openFileCount) == 0) {
      close = CloseAsync();
      return true;
    }

    close = Task.CompletedTask;
    return false;
  }

  public async Task CloseAsync() {
    CompilationManager.CancelPendingUpdates();
    try {
      await CompilationManager.LastDocument;
      observer.OnCompleted();
    } catch (OperationCanceledException) {
    }
  }

  public async Task<CompilationAfterParsing> GetLastDocumentAsync() {
    await workCompletedForCurrentVersion.WaitAsync();
    workCompletedForCurrentVersion.Release();
    logger.LogDebug($"GetLastDocumentAsync passed ProjectManager check for {Project.Uri}");
    return await CompilationManager.LastDocument;
  }

  public async Task<IdeState> GetSnapshotAfterParsingAsync() {
    try {
      var parsedCompilation = await CompilationManager.ParsedCompilation;
      logger.LogDebug($"GetSnapshotAfterParsingAsync returns compilation version {parsedCompilation.Version}");
    } catch (OperationCanceledException) {
      logger.LogDebug($"GetSnapshotAfterResolutionAsync caught OperationCanceledException for parsed compilation {Project.Uri}");
    }

    logger.LogDebug($"GetSnapshotAfterParsingAsync returns state version {observer.LastPublishedState.Version}");
    return observer.LastPublishedState;
  }

  public async Task<IdeState> GetStateAfterResolutionAsync() {
    try {
      var resolvedCompilation = await CompilationManager.ResolvedCompilation;
      logger.LogDebug($"GetStateAfterResolutionAsync returns compilation version {resolvedCompilation.Version}");
    } catch (OperationCanceledException) {
      logger.LogDebug($"GetSnapshotAfterResolutionAsync caught OperationCanceledException for resolved compilation {Project.Uri}");
      return await GetSnapshotAfterParsingAsync();
    }

    logger.LogDebug($"GetStateAfterResolutionAsync returns state version {observer.LastPublishedState.Version}");
    return observer.LastPublishedState;
  }

  public async Task<IdeState> GetIdeStateAfterVerificationAsync() {
    try {
      await GetLastDocumentAsync();
    } catch (OperationCanceledException) {
    }

    return observer.LastPublishedState;
  }


  /// <summary>
  /// This property and related code will be removed once we replace server gutter icons with client side computed gutter icons
  /// </summary>
  public static bool GutterIconTesting = false;

  public async Task VerifyEverythingAsync(Uri? uri) {
    _ = workCompletedForCurrentVersion.WaitAsync();
    try {
      var compilationManager = CompilationManager;
      var resolvedCompilation = await compilationManager.ResolvedCompilation;

      var verifiables = resolvedCompilation.Verifiables.ToList();
      if (uri != null) {
        verifiables = verifiables.Where(d => d.Tok.Uri == uri).ToList();
      }

      lock (RecentChanges) {
        var freshlyChangedVerifiables = GetChangedVerifiablesFromRanges(resolvedCompilation, RecentChanges);
        ChangedVerifiables = freshlyChangedVerifiables.Concat(ChangedVerifiables).Distinct()
          .Take(MaxRememberedChangedVerifiables).ToList();
        RecentChanges = new List<Location>();
      }

      int GetPriorityAttribute(ISymbol symbol) {
        if (symbol is IAttributeBearingDeclaration hasAttributes &&
            hasAttributes.HasUserAttribute("priority", out var attribute) &&
            attribute.Args.Count >= 1 && attribute.Args[0] is LiteralExpr { Value: BigInteger priority }) {
          return (int)priority;
        }
        return 0;
      }

      int TopToBottomPriority(ISymbol symbol) {
        return symbol.Tok.pos;
      }
      var implementationOrder = ChangedVerifiables.Select((v, i) => (v, i)).ToDictionary(k => k.v, k => k.i);
      var orderedVerifiables = verifiables.OrderByDescending(GetPriorityAttribute).CreateOrderedEnumerable(
        t => implementationOrder.GetOrDefault(t.Tok.GetFilePosition(), () => int.MaxValue),
        null, false).CreateOrderedEnumerable(TopToBottomPriority, null, false).ToList();
      logger.LogDebug($"Ordered verifiables: {string.Join(", ", orderedVerifiables.Select(v => v.NameToken.val))}");

      var orderedVerifiableLocations = orderedVerifiables.Select(v => v.NameToken.GetFilePosition()).ToList();
      if (GutterIconTesting) {
        foreach (var canVerify in orderedVerifiableLocations) {
          await compilationManager.VerifyTask(canVerify, false);
        }

        logger.LogDebug($"Finished translation in VerifyEverything for {Project.Uri}");
      }

      foreach (var canVerify in orderedVerifiableLocations) {
        // Wait for each task to try and run, so the order is respected.
        await compilationManager.VerifyTask(canVerify);
      }
    }
    finally {
      logger.LogDebug("Setting result for workCompletedForCurrentVersion");
      workCompletedForCurrentVersion.Release();
    }
  }

  private IEnumerable<FilePosition> GetChangedVerifiablesFromRanges(CompilationAfterResolution translated, IEnumerable<Location> changedRanges) {
    IntervalTree<Position, Position> GetTree(Uri uri) {
      var intervalTree = new IntervalTree<Position, Position>();
      foreach (var canVerify in translated.Verifiables) {
        if (canVerify.Tok.Uri == uri) {
          intervalTree.Add(
            canVerify.RangeToken.StartToken.GetLspPosition(),
            canVerify.RangeToken.EndToken.GetLspPosition(true),
            canVerify.NameToken.GetLspPosition());
        }
      }
      return intervalTree;
    }

    Dictionary<Uri, IntervalTree<Position, Position>> trees = new();

    return changedRanges.SelectMany(changeRange => {
      var tree = trees.GetOrCreate(changeRange.Uri.ToUri(), () => GetTree(changeRange.Uri.ToUri()));
      return tree.Query(changeRange.Range.Start, changeRange.Range.End).Select(position => new FilePosition(changeRange.Uri.ToUri(), position));
    });
  }

  public void OpenDocument(Uri uri, bool triggerCompilation) {
    Interlocked.Increment(ref openFileCount);
    var lastPublishedState = observer.LastPublishedState;
    var migratedVerificationTrees = lastPublishedState.VerificationTrees;

    if (triggerCompilation) {
      StartNewCompilation(migratedVerificationTrees, lastPublishedState);
      TriggerVerificationForFile(uri);
    }
  }

  public void Dispose() {
    CompilationManager.CancelPendingUpdates();
  }
}
