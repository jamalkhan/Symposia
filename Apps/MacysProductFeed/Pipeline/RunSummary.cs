namespace MacysProductFeed.Pipeline;

/// <summary>
/// Accumulates end-of-run counters (issue #127 FR-8) and renders the
/// summary line printed after a run.
/// </summary>
public sealed class RunSummary
{
    public int PagesProcessed { get; private set; }
    public int PageErrors { get; private set; }
    public int TileErrors { get; private set; }
    public int ProductsWritten { get; private set; }
    public int DuplicatesSkipped { get; private set; }

    public void RecordPageProcessed() => PagesProcessed++;
    public void RecordPageError() => PageErrors++;
    public void RecordTileError() => TileErrors++;
    public void RecordProductsWritten(int count) => ProductsWritten += count;
    public void RecordDuplicatesSkipped(int count) => DuplicatesSkipped += count;

    /// <summary>
    /// Exit code semantics (issue #127 post-huddle clarification): 0 on any
    /// partial success (at least one product written), non-zero only when
    /// zero products were produced across all input URLs.
    /// </summary>
    public int ExitCode => ProductsWritten > 0 ? 0 : 1;

    public override string ToString() =>
        $"Run summary: {ProductsWritten} product(s) written, {PagesProcessed} page(s) processed, " +
        $"{PageErrors} page error(s), {TileErrors} tile error(s), {DuplicatesSkipped} duplicate(s) skipped.";
}
