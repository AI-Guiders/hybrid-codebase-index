using System.Collections.Frozen;

namespace HybridCodebaseIndex.Mcp;

/// <summary>
/// Ops manuals for MCP tools (pull via <c>man</c>). Not shell man(1).
/// </summary>
internal static class ManPages
{
    internal const string Toc =
        """
        NAME
          man — MCP ops manual for Hybrid Codebase Index tools (not shell).

        SYNOPSIS
          man
          man tool=<tool_name>

        DESCRIPTION
          Returns operating procedure for a tool. ListTools = capabilities only;
          empty search hits ≠ auto-reindex. Call man when miss / first contact / unsure.

        PAGES
          codebase_index_search
          codebase_index_status
          codebase_index_reindex
          codebase_index_watch

        SEE ALSO
          Server instructions on initialize; host Grep/Glob as fallback when index OK but query misses.
        """;

    private static readonly FrozenDictionary<string, string> Pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["codebase_index_search"] =
            """
            NAME
              codebase_index_search — FTS (optional semantic) search over indexed workspace.

            SYNOPSIS
              codebase_index_search workspace_path=… query=… [path_prefix] [extensions] [semantic] …

            WHAT EMPTY HITS MEAN (do not skip)
              A) Index missing / empty / wrong scope  → codebase_index_status first
              B) Index ok, query/filters miss           → relax filters OR host Grep/Glob
              C) Index stale after large add/pull      → codebase_index_reindex (once)

              Wire may include missKind / suggestedNext on empty hit lists; still use this ladder.

            NEVER
              auto-reindex on every empty hitlist
              treat watch as substitute for explicit reindex after format change

            SEE ALSO
              man tool=codebase_index_status
              man tool=codebase_index_reindex
              man tool=codebase_index_watch
              codebase_index_explain
            """,

        ["codebase_index_status"] =
            """
            NAME
              codebase_index_status — index health and scope diagnostics.

            SYNOPSIS
              codebase_index_status workspace_path=… [solution_path=…]

            READ FIRST WHEN
              search returned 0 hits and you do not know if the index exists / is empty / wrong scope
              last reindex may have failed (lastReindexError*)
              settings overlay may be broken (settingsParseError)

            KEY FIELDS
              databaseExists, documentCount, documentCountMayBeStale
              workspaceRoot / solution scope (DB is per workspace_root + solution_path)
              lastReindexError, reindexState, settingsSource

            THEN
              missing or empty → codebase_index_reindex
              exists with documents → treat search miss as query/filters (not auto-reindex)

            SEE ALSO
              man tool=codebase_index_search
              man tool=codebase_index_reindex
            """,

        ["codebase_index_reindex"] =
            """
            NAME
              codebase_index_reindex — rebuild or incrementally refresh FTS index.

            SYNOPSIS
              codebase_index_reindex workspace_path=… [solution_path=…] [full_rebuild=true]

            WHEN
              status shows missing DB, 0 documents, or you know large add/pull left index stale
              after intentional settings/format change (prefer full_rebuild when required)

            WHEN NOT
              every empty search (query may simply miss; use Grep/Glob)
              as a substitute for fixing wrong workspace_path / solution_path

            NOTES
              Default is incremental; full_rebuild=true is full rebuild.
              Watch (codebase_index_watch) is best-effort and does not replace this after format change.

            SEE ALSO
              man tool=codebase_index_status
              man tool=codebase_index_search
              man tool=codebase_index_watch
            """,

        ["codebase_index_watch"] =
            """
            NAME
              codebase_index_watch — debounced background incremental sync.

            SYNOPSIS
              codebase_index_watch workspace_path=… enabled=true|false [debounce_ms=…] [solution_path=…]

            WHAT IT IS
              Best-effort file watcher that triggers incremental index updates.

            WHAT IT IS NOT
              Not a guarantee of freshness; not a substitute for codebase_index_reindex
              after large changes or index format/settings changes.

            SEE ALSO
              man tool=codebase_index_reindex
              man tool=codebase_index_status
            """,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyCollection<string> KnownTools => Pages.Keys;

    internal static string Resolve(string? tool)
    {
        if (string.IsNullOrWhiteSpace(tool))
            return Toc.TrimEnd() + "\n";

        var key = tool.Trim();
        if (Pages.TryGetValue(key, out var page))
            return page.TrimEnd() + "\n";

        var known = string.Join(", ", Pages.Keys.Order(StringComparer.Ordinal));
        return $"Unknown man page: {key}\nKnown: {known}\nCall man with no tool for the table of contents.\n";
    }
}
