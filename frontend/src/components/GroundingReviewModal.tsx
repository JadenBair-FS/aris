import { useState, useEffect } from 'react';
import { CheckCircle2, HelpCircle, Cpu, Brain, ChevronDown, ArrowRight } from 'lucide-react';
import { Button } from '@/components/ui/button';
import {
    Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter,
} from '@/components/ui/dialog';
import { dictionaryApi } from '@/api/dictionary';
import type { SkillCandidate, GroundingCorrection } from '@/types/api';

interface SkillItem {
    name: string;
    originalName?: string; // set when grounding substituted a canonical name
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

// Skills that were grounded AND had their name changed (have originalName)
function mappedSkills(skills: SkillItem[]) {
    return skills.filter(s => !!s.originalName);
}

// Skills that were grounded with no name change (originalName absent)
function exactSkills(skills: SkillItem[]) {
    return skills.filter(s => !s.originalName);
}

export default function GroundingReviewModal({
    isOpen,
    groundedTechnical,
    groundedSoft,
    ungroundedSkills,
    onConfirm,
    onSkip,
}: GroundingReviewModalProps) {
    // key = originalName (for mapped grounded) or skill.name (for ungrounded)
    // value = chosen canonical, or '' = keep current / keep unlisted
    const [selections, setSelections] = useState<Record<string, string>>({});
    const [candidates, setCandidates] = useState<Record<string, SkillCandidate[] | undefined>>({});
    const [isConfirming, setIsConfirming] = useState(false);

    const allGrounded = [...groundedTechnical, ...groundedSoft];
    const mappedGrounded = mappedSkills(allGrounded);

    useEffect(() => {
        if (!isOpen) return;

        setSelections({});
        setCandidates({});

        // Prefetch candidates for ungrounded skills and mapped-grounded skills in parallel
        const skillsNeedingCandidates = [
            ...ungroundedSkills.map(s => ({ key: s.name, query: s.name })),
            ...mappedGrounded.map(s => ({ key: s.originalName!, query: s.originalName! })),
        ];

        skillsNeedingCandidates.forEach(({ key, query }) => {
            dictionaryApi.getSkillCandidates(query, 10).then(results => {
                setCandidates(prev => ({ ...prev, [key]: results }));
            }).catch(() => {
                setCandidates(prev => ({ ...prev, [key]: [] }));
            });
        });
    }, [isOpen, ungroundedSkills.length, mappedGrounded.length]); // eslint-disable-line react-hooks/exhaustive-deps

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

    const totalGrounded = groundedTechnical.length + groundedSoft.length;
    const overrideCount = Object.values(selections).filter(v => v !== '').length;

    const renderSkillRow = (skill: SkillItem, key: string, currentCanonical: string, placeholder: string) => {
        const skillCandidates = candidates[key];
        const isLoading = skillCandidates === undefined;
        const selected = selections[key] ?? '';
        const changed = selected !== '' && selected !== currentCanonical;

        return (
            <div key={key} className="flex items-center gap-3 py-2 border-b border-slate-50 last:border-0">
                <div className="flex-1 min-w-0">
                    <div className="flex items-center gap-1.5 flex-wrap">
                        <span className="text-sm text-slate-500 font-medium truncate">{key}</span>
                        <ArrowRight className="h-3 w-3 text-slate-300 flex-shrink-0" />
                        <span className={`text-sm font-semibold truncate ${changed ? 'text-amber-700' : 'text-slate-700'}`}>
                            {changed ? selected : currentCanonical}
                        </span>
                    </div>
                    {skill.category && (
                        <span className="text-xs text-slate-400">{skill.category}</span>
                    )}
                </div>
                <div className="relative flex-shrink-0 w-56">
                    <select
                        className="w-full appearance-none rounded-md border border-slate-200 bg-white px-3 py-1.5 pr-8 text-sm text-slate-700 focus:border-slate-400 focus:outline-none focus:ring-1 focus:ring-slate-400 disabled:opacity-50"
                        value={selected}
                        disabled={isLoading}
                        onChange={e => setSelections(prev => ({ ...prev, [key]: e.target.value }))}
                    >
                        {isLoading ? (
                            <option value="">Loading suggestions...</option>
                        ) : (
                            <>
                                <option value="">{placeholder}</option>
                                {skillCandidates.map(c => (
                                    <option key={c.name} value={c.name}>
                                        {c.name} — {Math.round((1 - c.distance) * 100)}%
                                    </option>
                                ))}
                            </>
                        )}
                    </select>
                    <ChevronDown className="pointer-events-none absolute right-2 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-slate-400" />
                    {changed && (
                        <div className="absolute -top-1 -right-1 h-2 w-2 rounded-full bg-amber-500" />
                    )}
                </div>
            </div>
        );
    };

    return (
        <Dialog open={isOpen}>
            <DialogContent className="max-h-[85vh] overflow-y-auto">
                <DialogHeader>
                    <DialogTitle>Review Extracted Skills</DialogTitle>
                    <DialogDescription>
                        ARIS mapped {totalGrounded} skill{totalGrounded !== 1 ? 's' : ''} to the knowledge graph.
                        {ungroundedSkills.length > 0 && (
                            <> {ungroundedSkills.length} skill{ungroundedSkills.length !== 1 ? 's were' : ' was'} not found in the database.</>
                        )}
                    </DialogDescription>
                </DialogHeader>

                <div className="px-6 py-4 space-y-5">

                    {/* Mapped grounded skills — show extracted → canonical with override option */}
                    {mappedGrounded.length > 0 && (
                        <div className="space-y-3">
                            <div className="flex items-center gap-2 text-sm font-medium text-emerald-700">
                                <CheckCircle2 className="h-4 w-4" />
                                Mapped to Knowledge Graph ({mappedGrounded.length})
                            </div>
                            <p className="text-xs text-slate-500">
                                The extracted skill name (left) was matched to a canonical entry (right). Change the mapping if the match looks wrong.
                            </p>
                            <div className="space-y-1">
                                {mappedSkills(groundedTechnical).length > 0 && (
                                    <div className="flex items-center gap-1.5 text-xs text-slate-400 mb-1">
                                        <Cpu className="h-3.5 w-3.5" /> Technical
                                    </div>
                                )}
                                {mappedSkills(groundedTechnical).map(s =>
                                    renderSkillRow(s, s.originalName!, s.name, 'Keep current mapping')
                                )}
                                {mappedSkills(groundedSoft).length > 0 && (
                                    <div className="flex items-center gap-1.5 text-xs text-slate-400 mt-2 mb-1">
                                        <Brain className="h-3.5 w-3.5" /> Soft Skills
                                    </div>
                                )}
                                {mappedSkills(groundedSoft).map(s =>
                                    renderSkillRow(s, s.originalName!, s.name, 'Keep current mapping')
                                )}
                            </div>
                        </div>
                    )}

                    {/* Exact-match grounded skills — simple badges, no interaction needed */}
                    {(exactSkills(groundedTechnical).length > 0 || exactSkills(groundedSoft).length > 0) && (
                        <div className="space-y-3">
                            <div className="flex items-center gap-2 text-sm font-medium text-emerald-700">
                                <CheckCircle2 className="h-4 w-4" />
                                Exact Match ({exactSkills(allGrounded).length})
                            </div>
                            {exactSkills(groundedTechnical).length > 0 && (
                                <div className="space-y-1.5">
                                    <div className="flex items-center gap-1.5 text-xs text-slate-400">
                                        <Cpu className="h-3.5 w-3.5" /> Technical
                                    </div>
                                    <div className="flex flex-wrap gap-1.5">
                                        {exactSkills(groundedTechnical).map(s => (
                                            <span key={s.name} className="inline-flex items-center rounded-md bg-emerald-50 px-2.5 py-1 text-xs font-medium text-emerald-700 ring-1 ring-inset ring-emerald-600/20">
                                                {s.name}
                                            </span>
                                        ))}
                                    </div>
                                </div>
                            )}
                            {exactSkills(groundedSoft).length > 0 && (
                                <div className="space-y-1.5">
                                    <div className="flex items-center gap-1.5 text-xs text-slate-400">
                                        <Brain className="h-3.5 w-3.5" /> Soft Skills
                                    </div>
                                    <div className="flex flex-wrap gap-1.5">
                                        {exactSkills(groundedSoft).map(s => (
                                            <span key={s.name} className="inline-flex items-center rounded-md bg-blue-50 px-2.5 py-1 text-xs font-medium text-blue-700 ring-1 ring-inset ring-blue-600/20">
                                                {s.name}
                                            </span>
                                        ))}
                                    </div>
                                </div>
                            )}
                        </div>
                    )}

                    {/* Divider before ungrounded */}
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
                            <div className="space-y-1">
                                {ungroundedSkills.map(skill =>
                                    renderSkillRow(skill, skill.name, '', 'Keep as unlisted')
                                )}
                            </div>
                        </div>
                    )}

                    {/* Empty state */}
                    {ungroundedSkills.length === 0 && totalGrounded === 0 && (
                        <p className="text-sm text-slate-500 text-center py-4">No skills were extracted. You can proceed.</p>
                    )}

                    {overrideCount > 0 && (
                        <p className="text-xs text-amber-600 font-medium">
                            {overrideCount} change{overrideCount !== 1 ? 's' : ''} will be applied on confirm.
                        </p>
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
