using SyncIt.Core.Models;

namespace SyncIt.Core.Services;

public interface IGlobFilter
{
    /// <summary>
    /// Returns true if a file at the given relative path should be synced for this pair.
    /// Applies include and exclude glob patterns.
    /// </summary>
    bool IsIncluded(string relativePath, SyncPair pair);

    /// <summary>
    /// Returns true if the file should be excluded (i.e. NOT synced).
    /// </summary>
    bool IsExcluded(string relativePath, SyncPair pair);
}
