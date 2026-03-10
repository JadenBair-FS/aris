import { cn } from '@/lib/utils';

// Tier color map for match analysis views
const TIER_STYLES: Record<number, { pill: string; dot?: string }> = {
    1: { pill: 'bg-green-50 text-green-800 border border-green-200' },
    2: { pill: 'bg-blue-50 text-blue-800 border border-blue-200' },
    3: { pill: 'bg-cyan-50 text-cyan-800 border border-cyan-200' },
    4: { pill: 'bg-amber-50 text-amber-800 border border-amber-200' },
    5: { pill: 'bg-red-50 text-red-800 border border-red-200' },
};

interface SkillBadgeProps {
    /** Canonical (grounded) skill name */
    name: string;
    /** Original name extracted by the LLM — shown as primary label when present */
    originalName?: string | null;
    /** Optional tier number (1–5) for color-coded match views */
    tier?: number;
    /** Optional years-of-experience annotation */
    yearsOfExperience?: number;
    /** Optional importance annotation (Essential / Preferred) — shown as a small pill */
    importance?: string;
    className?: string;
}

/**
 * Displays a skill with optional grounding provenance.
 *
 * - Primary label: `originalName` when available, otherwise `name`.
 * - When `originalName` differs from `name` (case-insensitive), a small
 *   "grounded to: [canonical]" tag is shown beneath the primary label.
 * - When `tier` is supplied the pill is color-coded by match tier.
 */
export function SkillBadge({
    name,
    originalName,
    tier,
    yearsOfExperience,
    importance,
    className,
}: SkillBadgeProps) {
    const displayName = originalName ?? name;
    const showGrounding =
        !!originalName && originalName.toLowerCase() !== name.toLowerCase();

    const tierStyle = tier !== undefined ? TIER_STYLES[tier] : null;

    const pillClass = tierStyle
        ? tierStyle.pill
        : 'bg-slate-100 text-slate-700';

    return (
        <span
            className={cn(
                'inline-flex flex-col rounded-lg px-3 py-1 text-sm leading-snug',
                pillClass,
                className
            )}
        >
            <span className="font-medium">
                {displayName}
                {yearsOfExperience !== undefined && yearsOfExperience > 0 && (
                    <span className="ml-1 opacity-50 font-normal">
                        · {yearsOfExperience}yr
                    </span>
                )}
                {importance && (
                    <span
                        className={cn(
                            'ml-2 text-xs px-1.5 py-0.5 rounded-full font-medium',
                            importance.toLowerCase() === 'essential'
                                ? 'bg-slate-800 text-white'
                                : 'bg-slate-200 text-slate-600'
                        )}
                    >
                        {importance}
                    </span>
                )}
            </span>
            {showGrounding && (
                <span className="text-xs text-slate-400 font-normal mt-0.5">
                    grounded to: {name}
                </span>
            )}
        </span>
    );
}

/**
 * Compact importance-only badge — kept for backwards compatibility with
 * TierBreakdown's inline importance annotation pattern.
 */
export function ImportanceBadge({ importance }: { importance: string }) {
    const isEssential = importance?.toLowerCase() === 'essential';
    return (
        <span
            className={cn(
                'ml-2 inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium border',
                isEssential
                    ? 'bg-slate-900 text-white border-slate-900'
                    : 'bg-slate-100 text-slate-600 border-slate-200'
            )}
        >
            {importance}
        </span>
    );
}
