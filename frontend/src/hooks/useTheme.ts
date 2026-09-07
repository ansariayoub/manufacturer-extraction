import { useCallback, useEffect, useState } from 'react';

export type Theme = 'light' | 'dark';

const STORAGE_KEY = 'theme';

function readStored(): Theme | null {
  try {
    const v = localStorage.getItem(STORAGE_KEY);
    return v === 'light' || v === 'dark' ? v : null;
  } catch {
    // localStorage can throw in a locked-down/private-browsing context — fall back to system.
    return null;
  }
}

/**
 * Light/dark mode, persisted across sessions. Defaults to the OS preference on first visit (no
 * stored choice yet), then remembers whatever the operator picks in Settings from then on —
 * applied by stamping data-theme on <html>, which tokens.css's `:root[data-theme="dark"]` block
 * reads.
 */
export function useTheme() {
  const [theme, setThemeState] = useState<Theme>(
    () => readStored() ?? (window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
  );

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme);
  }, [theme]);

  const setTheme = useCallback((next: Theme) => {
    setThemeState(next);
    try {
      localStorage.setItem(STORAGE_KEY, next);
    } catch {
      // Non-fatal: the choice just won't survive a reload in this browsing context.
    }
  }, []);

  const toggleTheme = useCallback(() => setTheme(theme === 'dark' ? 'light' : 'dark'), [theme, setTheme]);

  return { theme, setTheme, toggleTheme };
}
