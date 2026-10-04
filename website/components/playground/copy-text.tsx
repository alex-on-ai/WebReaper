"use client";

import { useState } from "react";

/** The concept's small text copy button (`.copy` -> `.copy.ok`). */
export function CopyText({ text }: { text: string }) {
  const [ok, setOk] = useState(false);
  return (
    <button
      type="button"
      className={`copy${ok ? " ok" : ""}`}
      onClick={() => {
        navigator.clipboard.writeText(text).then(
          () => {
            setOk(true);
            setTimeout(() => setOk(false), 1200);
          },
          () => {},
        );
      }}
    >
      {ok ? "copied" : "copy"}
    </button>
  );
}
