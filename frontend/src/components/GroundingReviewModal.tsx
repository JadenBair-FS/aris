import { useState, useEffect } from 'react';
import { CheckCircle2, HelpCircle, Cpu, Brain, ChevronDown } from 'lucide-react';
import { Button } from '@/components/ui/button';
import {
    Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter,
} from '@/components/ui/dialog';
import { dictionaryApi } from '@/api/dictionary';
import type { SkillCandidate, GroundingCorrection } from '@/types/api';

interface SkillItem {
    name: string;
    category?: string;
}

interface GroundingReviewModalProps {
    isOpen: boolean;
    groundedTechnical: SkillItem[];
    groundedSoft: SkillItem[];
    ungroundedSkills: SkillItem[];
    onConfirm: (corrections: GroundingCorrection[]) => Promise<void>;
    onSkip: () => void;
}

export default function GroundingReviewModal({
    isOpen,
    groundedTechnical,
    groundedSoft,
    ungroundedSkills,
    onConfirm,
    onSkip,
}: GroundingReviewModalProps) {
    // Record<skillName, selectedCanonicalName> — empty string = "keep as unlisted"
    const [selections, setSelections] = useState<Record<string, string>>({});
    // Record<skillName, candidates | undefined (loading)>
    const [candidates, setCandidates] = useState<Record<string, SkillCandidate[] | undefined>>({});
    const [isConfirming, setIsConfirming] = useState(false);

    // Prefetch candidates for all ungrounded skills when modal opens
    useEffect(() => {
        if (!isOpen || ungroundedSkills.length === 0) return;

        setSelections({});
        setCandidates({});

        // Start all fetches in parallel
        ungroundedSkills.forEach(skill => {
            dictionaryApi.getSkillCandidates(skill.name, 10).then(results => {
                setCandidates(prev => ({ ...prev, [skill.name]: results }));
            }).catch(() => {
                setCandidates(prev => ({ ...prev, [skill.name]: [] }));
            });
        });
    }, [isOpen, ungroundedSkills.length]); // eslint-disable-line react-hooks/exhaustive-deps

    const handleConfirm = async () => {
        setIsConfirming(true);
        try {
            const corrections: GroundingCorrection[] = Object.entries(selections)
                .filter(([, to]) => to !== '')
                .map(([from, to]) => ({ from, to }));
            await onConfirm(corrections);
        } finally {
            setIsConfirming(false);
        }
    };

    const mappedCount = Object.values(selections).filter(v => v !== '').length;
    const totalGrounded = groundedTechnical.length + groundedSoft.length;

    return (
        <Dialog open={isOpen}>
            <DialogContent>
                <DialogHeader>
                    <DialogTitle>Review Extracted Skills</DialogTitle>
                    <DialogDescription>
                        ARIS mapped {totalGrounded} skill{totalGrounded !== 1 ? 's' : ''} to the knowledge graph.
                        {ungroundedSkills.length > 0 && (
                            <> Review the {ungroundedSkills.length} skill{ungroundedSkills.length !== 1 ? 's' : ''} not found in the database below.</>
                        )}
                    </DialogDescription>
                </DialogHeader>

                <div className="px-6 py-4 space-y-5">
                    {/* Grounded skills */}
                    {(groundedTechnical.length > 0 || groundedSoft.length > 0) && (
                        <div className="space-y-3">
                            <div className="flex items-center gap-2 text-sm font-medium text-emerald-700">
                                <CheckCircle2 className="h-4 w-4" />
                                Mapped to Knowledge Graph ({totalGrounded})
                            </div>

                            {groundedTechnical.length > 0 && (
                                <div className="space-y-1.5">
                                    <div className="flex items-center gap-1.5 text-xs text-slate-500">
                                        <Cpu className="h-3.5 w-3.5" />
                                        Technical
                                    </div>
                                    <div className="flex flex-wrap gap-1.5">
                                        {groundedTechnical.map(s => (
                                            <span key={s.name} className="inline-flex items-center rounded-md bg-emerald-50 px-2.5 py-1 text-xs font-medium text-emerald-700 ring-1 ring-inset ring-emerald-600/20">
                                                {s.name}
                                            </span>
                                        ))}
                                    </div>
                                </div>
                            )}

                            {groundedSoft.length > 0 && (
                                <div className="space-y-1.5">
                                    <div className="flex items-center gap-1.5 text-xs text-slate-500">
                                        <Brain className="h-3.5 w-3.5" />
                                        Soft Skills
                                    </div>
                                    <div className="flex flex-wrap gap-1.5">
                                        {groundedSoft.map(s => (
                                            <span key={s.name} className="inline-flex items-center rounded-md bg-blue-50 px-2.5 py-1 text-xs font-medium text-blue-700 ring-1 ring-inset ring-blue-600/20">
                                                {s.name}
                                            </span>
                                        ))}
                                    </div>
                                </div>
                            )}
                        </div>
                    )}

                    {/* Divider */}
                    {totalGrounded > 0 && ungroundedSkills.length > 0 && (
                        <div className="border-t border-slate-100" />
                    )}

                    {/* Ungrounded skills */}
                    {ungroundedSkills.length > 0 && (
                        <div className="space-y-3">
                            <div className="flex items-center gap-2 text-sm font-medium text-amber-700">
                                <HelpCircle className="h-4 w-4" />
                                Not Found in Database ({ungroundedSkills.length})
                            </div>
                            <p className="text-xs text-slate-500">
                                These skills weren't matched to the knowledge graph. Optionally map them to a similar canonical skill to improve matching accuracy.
                            </p>

                            <div className="space-y-2">
                                {ungroundedSkills.map(skill => {
                                    const skillCandidates = candidates[skill.name];
                                    const isLoading = skillCandidates === undefined;
                                    const selected = selections[skill.name] ?? '';

                                    return (
                                        <div key={skill.name} className="flex items-center gap-3 py-2 border-b border-slate-50 last:border-0">
                                            <div className="flex-1 min-w-0">
                                                <span className="text-sm text-slate-700 font-medium truncate block">{skill.name}</span>
                                                {skill.category && (
                                                    <span className="text-xs text-slate-400">{skill.category}</span>
                                                )}
                                            </div>
                                            <div className="relative flex-shrink-0 w-64">
                                                <select
                                                    className="w-full appearance-none rounded-md border border-slate-200 bg-white px-3 py-1.5 pr-8 text-sm text-slate-700 focus:border-slate-400 focus:outline-none focus:ring-1 focus:ring-slate-400 disabled:opacity-50"
                                                    value={selected}
                                                    disabled={isLoading}
                                                    onChange={e => setSelections(prev => ({ ...prev, [skill.name]: e.target.value }))}
                                                >
                                                    {isLoading ? (
                                                        <option value="">Loading suggestions...</option>
                                                    ) : (
                                                        <>
                                                            <option value="">Keep as unlisted</option>
                                                            {skillCandidates.map(c => (
                                                                <option key={c.name} value={c.name}>
                                                                    {c.name} — {Math.round((1 - c.distance) * 100)}%
                                                                </option>
                                                            ))}
                                                        </>
                                                    )}
                                                </select>
                                                <ChevronDown className="pointer-events-none absolute right-2 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-slate-400" />
                                                {selected && (
                                                    <div className="absolute -top-1 -right-1 h-2 w-2 rounded-full bg-emerald-500" />
                                                )}
                                            </div>
                                        </div>
                                    );
                                })}
                            </div>

                            {mappedCount > 0 && (
                                <p className="text-xs text-emerald-600 font-medium">
                                    {mappedCount} skill{mappedCount !== 1 ? 's' : ''} will be mapped to the knowledge graph on confirm.
                                </p>
                            )}
                        </div>
                    )}

                    {/* Empty state — everything grounded */}
                    {ungroundedSkills.length === 0 && totalGrounded === 0 && (
                        <p className="text-sm text-slate-500 text-center py-4">No skills were extracted. You can proceed.</p>
                    )}
                </div>

                <DialogFooter>
                    <Button variant="ghost" onClick={onSkip} disabled={isConfirming}>
                        Skip
                    </Button>
                    <Button
                        className="bg-slate-900 hover:bg-slate-800 text-white"
                        onClick={handleConfirm}
                        disabled={isConfirming}
                    >
                        {isConfirming ? 'Saving...' : 'Confirm & Continue'}
                    </Button>
                </DialogFooter>
            </DialogContent>
        </Dialog>
    );
}
