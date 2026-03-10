import { Collapsible, CollapsibleTrigger, CollapsibleContent } from './ui/collapsible';
import { Badge } from './ui/badge';
import { ChevronDown, CheckCircle2, XCircle, MinusCircle } from 'lucide-react';
import { useState } from 'react';
import { ImportanceBadge } from './SkillBadge';
import type { SkillGapItem, UngroundedSkillComparison } from '../types/api';

const TierSection = ({
    title,
    count,
    borderColor,
    emptyLabel,
    children,
}: {
    title: string;
    count: number;
    borderColor: string;
    emptyLabel?: string;
    children: React.ReactNode;
}) => {
    const [isOpen, setIsOpen] = useState(count > 0);

    if (count === 0) {
        return (
            <div className={`border-l-4 ${borderColor} bg-white rounded-r-lg border border-l-4 border-y border-r px-3 py-2 flex items-center justify-between`}>
                <div className="flex items-center gap-2">
                    <h3 className="text-sm font-medium text-slate-600">{title}</h3>
                    <Badge variant="secondary" className="text-slate-400 text-xs">0</Badge>
                </div>
                <span className="text-xs text-slate-400">{emptyLabel ?? 'None'}</span>
            </div>
        );
    }

    return (
        <Collapsible open={isOpen} onOpenChange={setIsOpen} className={`border-l-4 ${borderColor} bg-white rounded-r-lg shadow-sm overflow-hidden border border-l-4 border-y border-r`}>
            <CollapsibleTrigger className="w-full flex items-center justify-between px-3 py-2.5 hover:bg-slate-50 transition-colors">
                <div className="flex items-center gap-2">
                    <h3 className="text-sm font-semibold text-slate-900">{title}</h3>
                    <Badge variant="secondary" className="text-xs">{count}</Badge>
                </div>
                <ChevronDown className={`h-4 w-4 text-slate-400 transition-transform ${isOpen ? 'rotate-180' : ''}`} />
            </CollapsibleTrigger>
            <CollapsibleContent>
                <div className="px-3 pb-3 space-y-1 border-t border-slate-100 pt-3">
                    {children}
                </div>
            </CollapsibleContent>
        </Collapsible>
    );
};

export function TierBreakdown({
    matchingSkills,
    implicitlyDiscoveredSkills,
    prerequisiteMetSkills,
    bridgeableSkills,
    hardGaps,
    ungroundedComparison,
}: {
    matchingSkills: SkillGapItem[];
    implicitlyDiscoveredSkills: string[];
    prerequisiteMetSkills: SkillGapItem[];
    bridgeableSkills: SkillGapItem[];
    hardGaps: SkillGapItem[];
    ungroundedComparison?: UngroundedSkillComparison;
}) {
    const ungroundedTotal = (ungroundedComparison?.matched.length ?? 0) +
        (ungroundedComparison?.missingFromResume.length ?? 0) +
        (ungroundedComparison?.extraInResume.length ?? 0);

    return (
        <div className="w-full space-y-2">
            <TierSection
                title="Tier 1: Direct Match"
                count={matchingSkills.length}
                borderColor="border-green-500"
                emptyLabel="No direct skill matches"
            >
                {matchingSkills.map((s, i) => (
                    <div key={i} className="flex justify-between items-center py-1.5 border-b border-slate-50 last:border-0">
                        <div className="flex items-center">
                            <span className="text-sm font-medium text-slate-800">{s.skillName}</span>
                            <ImportanceBadge importance={s.importance} />
                        </div>
                        {s.yearsRequired > 0 && (
                            <span className="text-xs text-slate-400">{s.candidateYears}yr / {s.yearsRequired}yr req</span>
                        )}
                    </div>
                ))}
            </TierSection>

            <TierSection
                title="Tier 2: Implicit Match"
                count={implicitlyDiscoveredSkills.length}
                borderColor="border-emerald-400"
                emptyLabel="No implicit matches found"
            >
                <div className="flex flex-wrap gap-2 py-1">
                    {implicitlyDiscoveredSkills.map((s, i) => (
                        <Badge key={i} variant="outline">{s}</Badge>
                    ))}
                </div>
            </TierSection>

            <TierSection
                title="Tier 3: Prerequisite Met"
                count={prerequisiteMetSkills.length}
                borderColor="border-yellow-400"
                emptyLabel="No prerequisite paths found"
            >
                {prerequisiteMetSkills.map((s, i) => (
                    <div key={i} className="flex flex-col py-1.5 border-b border-slate-50 last:border-0">
                        <div className="flex items-center">
                            <span className="text-sm font-medium text-slate-800">{s.skillName}</span>
                            <ImportanceBadge importance={s.importance} />
                        </div>
                        {s.bridgePath && <span className="text-xs text-slate-400">{s.bridgePath}</span>}
                    </div>
                ))}
            </TierSection>

            <TierSection
                title="Tier 4: Bridgeable"
                count={bridgeableSkills.length}
                borderColor="border-orange-400"
                emptyLabel="No bridgeable skills found"
            >
                {bridgeableSkills.map((s, i) => (
                    <div key={i} className="flex flex-col py-1.5 border-b border-slate-50 last:border-0">
                        <div className="flex items-center">
                            <span className="text-sm font-medium text-slate-800">{s.skillName}</span>
                            <ImportanceBadge importance={s.importance} />
                        </div>
                        {s.bridgePath && <span className="text-xs text-slate-400">{s.bridgePath}</span>}
                    </div>
                ))}
            </TierSection>

            <TierSection
                title="Tier 5: Hard Gap"
                count={hardGaps.length}
                borderColor="border-red-500"
                emptyLabel="No hard gaps — great fit!"
            >
                {hardGaps.map((s, i) => (
                    <div key={i} className="flex items-center py-2 border-b border-slate-50 last:border-0">
                        <span className="font-medium text-red-700">{s.skillName}</span>
                        <ImportanceBadge importance={s.importance} />
                    </div>
                ))}
            </TierSection>

            {ungroundedComparison && ungroundedTotal > 0 && (
                <TierSection
                    title="Unlisted Skill Comparison"
                    count={ungroundedTotal}
                    borderColor="border-slate-300"
                    emptyLabel="No unlisted skills"
                >
                    <p className="text-xs text-slate-400 mb-2">Skills not in the canonical database, compared by name.</p>
                    <div className="space-y-1">
                        {ungroundedComparison.matched.map((s, i) => (
                            <div key={`m-${i}`} className="flex items-center gap-2 py-1 border-b border-slate-50 last:border-0">
                                <CheckCircle2 className="h-3.5 w-3.5 text-green-500 shrink-0" />
                                <span className="text-sm text-slate-700">{s}</span>
                            </div>
                        ))}
                        {ungroundedComparison.missingFromResume.map((s, i) => (
                            <div key={`miss-${i}`} className="flex items-center gap-2 py-1 border-b border-slate-50 last:border-0">
                                <XCircle className="h-3.5 w-3.5 text-red-400 shrink-0" />
                                <span className="text-sm text-slate-700">{s}</span>
                            </div>
                        ))}
                        {ungroundedComparison.extraInResume.map((s, i) => (
                            <div key={`extra-${i}`} className="flex items-center gap-2 py-1 border-b border-slate-50 last:border-0">
                                <MinusCircle className="h-3.5 w-3.5 text-slate-300 shrink-0" />
                                <span className="text-sm text-slate-500">{s}</span>
                            </div>
                        ))}
                    </div>
                    <div className="flex gap-4 mt-2 pt-2 border-t border-slate-50">
                        <span className="flex items-center gap-1 text-xs text-slate-400">
                            <CheckCircle2 className="h-3 w-3 text-green-500" /> Matched
                        </span>
                        <span className="flex items-center gap-1 text-xs text-slate-400">
                            <XCircle className="h-3 w-3 text-red-400" /> Missing from resume
                        </span>
                        <span className="flex items-center gap-1 text-xs text-slate-400">
                            <MinusCircle className="h-3 w-3 text-slate-300" /> Extra (bonus)
                        </span>
                    </div>
                </TierSection>
            )}
        </div>
    );
}
