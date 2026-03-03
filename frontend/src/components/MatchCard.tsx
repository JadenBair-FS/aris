import { useState, useEffect } from 'react';

interface MatchCardProps {
    title: string;
    subtitle: string;
    arisScore: number;
    vectorSim: number;
    tier1Count?: number;
    hardGapCount?: number;
    index: number;
    onClick: () => void;
}

export function MatchCard({
    title,
    subtitle,
    arisScore,
    vectorSim,
    tier1Count,
    hardGapCount,
    index,
    onClick,
}: MatchCardProps) {
    const [visible, setVisible] = useState(false);

    useEffect(() => {
        const t = setTimeout(() => setVisible(true), index * 150);
        return () => clearTimeout(t);
    }, [index]);

    const scoreBadgeClass =
        arisScore >= 0.75
            ? 'bg-green-50 text-green-700'
            : arisScore >= 0.60
            ? 'bg-yellow-50 text-yellow-700'
            : 'bg-slate-100 text-slate-600';

    return (
        <div
            onClick={onClick}
            className={`bg-white rounded-xl border border-slate-200 shadow-sm hover:shadow-md transition-all duration-500 cursor-pointer p-5 ${
                visible ? 'opacity-100 translate-y-0' : 'opacity-0 translate-y-3'
            }`}
        >
            <div className="flex items-start justify-between gap-4">
                <div className="flex-1 min-w-0">
                    <h3 className="font-semibold text-slate-900 truncate">{title}</h3>
                    <p className="text-xs text-slate-500 mt-0.5 font-mono">{subtitle}</p>
                </div>
                <span className={`shrink-0 text-sm font-semibold px-2.5 py-1 rounded-full ${scoreBadgeClass}`}>
                    {arisScore.toFixed(2)}
                </span>
            </div>

            <div className="flex items-center gap-4 mt-4 text-xs text-slate-500">
                <span>Embedding: <span className="font-medium text-slate-700">{vectorSim.toFixed(2)}</span></span>
                {tier1Count !== undefined && (
                    <span className="flex items-center gap-1">
                        <span className="w-2 h-2 rounded-full bg-green-500 inline-block" />
                        {tier1Count} matched
                    </span>
                )}
                {hardGapCount !== undefined && hardGapCount > 0 && (
                    <span className="flex items-center gap-1">
                        <span className="w-2 h-2 rounded-full bg-red-500 inline-block" />
                        {hardGapCount} gaps
                    </span>
                )}
            </div>
        </div>
    );
}
