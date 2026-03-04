/** Converts a 0–1 ArisScore to a percentage string, e.g. 0.724 → "72%" */
export const formatArisScore = (score: number): string =>
    `${Math.round(score * 100)}%`;
