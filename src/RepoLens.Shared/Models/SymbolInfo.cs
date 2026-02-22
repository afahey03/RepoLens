namespace RepoLens.Shared.Models;

/// <summary>
/// A code symbol extracted during analysis (class, function, import, etc.).
/// </summary>
public class SymbolInfo
{
    public required string Name { get; set; }
    public SymbolKind Kind { get; set; }
    public required string FilePath { get; set; }
    public int Line { get; set; }
    public string? ParentSymbol { get; set; }
}

public enum SymbolKind
{
    Class,
    Interface,
    Method,
    Property,
    Function,
    Variable,
    Import,
    Namespace,
    Module
}
