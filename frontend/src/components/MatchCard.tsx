import { useState, useEffect } from 'react';
import { ChevronRight } from 'lucide-react';

interface MatchCardProps {
    title: string;
    subtitle: string;
    cta: string;
    index: number;
    onClick: () => void;
}

export function MatchCard({
    title,
    subtitle,
    cta,
    index,
    onClick,
}: MatchCardProps) {
    const [visible, setVisible] = useState(false);

    useEffect(() => {
        const t = setTimeout(() => setVisible(true), index * 150);
        return () => clearTimeout(t);
    }, [index]);

    return (
        <div
            onClick={onClick}
            className={`bg-white rounded-xl border border-slate-200 shadow-sm hover:shadow-md transition-all duration-500 cursor-pointer p-5 ${
                visible ? 'opacity-100 translate-y-0' : 'opacity-0 translate-y-3'
            }`}
        >
            <div className="flex items-center justify-between gap-4">
                <div className="flex-1 min-w-0">
                    <h3 className="font-semibold text-slate-900 truncate">{title}</h3>
                    <p className="text-xs text-slate-500 mt-0.5 font-mono">{subtitle}</p>
                </div>
                <span className="shrink-0 flex items-center gap-1 text-sm font-medium text-slate-600 bg-slate-100 hover:bg-slate-200 transition-colors px-3 py-1.5 rounded-lg">
                    {cta} <ChevronRight className="h-3.5 w-3.5" />
                </span>
            </div>
        </div>
    );
}
