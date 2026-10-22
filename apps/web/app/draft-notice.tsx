/** Shown on legal pages until counsel signs them off (Phase 6 release gate: "legal review complete"). */
export function DraftNotice() {
  return (
    <p className="draft">
      Draft for review. This text describes how the game currently handles data but has not yet been
      approved by legal counsel. Items in [brackets] are to be confirmed.
    </p>
  );
}
