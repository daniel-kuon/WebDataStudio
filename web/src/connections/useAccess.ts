import { useEffect, useState } from "react";
import { me, type StudioAccess } from "../api";

/// What this deployment lets people do. Cached for the lifetime of the page, the same way the role
/// is: it changes with a rollout, not with a click.
///
/// The default is permissive, which is also the server's: a studio that says nothing about any of
/// this behaves as it always did, and a browser that cannot reach `/auth/me` should not hide the
/// buttons of a studio that would have allowed them.
const permissive: StudioAccess = {
  scope: "Stored",
  mayAdd: true,
  mayUpload: true,
  mayBrowse: true,
};

let cached: StudioAccess | undefined;

export function useAccess(): StudioAccess {
  const [access, setAccess] = useState<StudioAccess>(cached ?? permissive);

  useEffect(() => {
    if (cached !== undefined) return;
    let cancelled = false;

    me().then(state => {
      cached = state.access ?? permissive;
      if (!cancelled) setAccess(cached);
    }).catch(() => { cached = permissive; });

    return () => { cancelled = true; };
  }, []);

  return access;
}

/// For tests, which each need their own studio.
export const forgetAccess = () => { cached = undefined; };
