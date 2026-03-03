import { useState } from 'react';
import { useParams, useNavigate } from 'react-router';
import { useQuery, useMutation } from '@tanstack/react-query';
import { matchApi } from '@/api/match';
import { ScoreHeader } from '@/components/ScoreHeader';
import { TierBreakdown } from '@/components/TierBreakdown';
import { Card, CardContent } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Loader2, ArrowLeft } from 'lucide-react';
import { Badge } from '@/components/ui/badge';

export default function CandidateDetail() {
    const { jobId, profileId } = useParams();
    const navigate = useNavigate();

    const { data: analysis, isLoading: isAnalysisLoading } = useQuery({
        queryKey: ['matchAnalysis', profileId, jobId],
        queryFn: () => matchApi.analyze(profileId!, jobId!),
        enabled: !!profileId && !!jobId,
    });

    const [summary, setSummary] = useState<{ text: string; score: number } | null>(null);
    const summaryMutation = useMutation({
        mutationFn: () => matchApi.getSummary(profileId!, jobId!),
        onSuccess: (data) => setSummary({ text: data.summary, score: data.groundingScore }),
    });

    if (isAnalysisLoading) {
        return (
            <div className="space-y-6">
                <Skeleton className="h-32 w-full" />
                <Skeleton className="h-64 w-full" />
            </div>
        );
    }

    if (!analysis) {
        return <div className="text-red-500 text-sm">Failed to load analysis.</div>;
    }

    return (
        <div className="space-y-8">
            <button
                onClick={() => navigate(-1)}
                className="flex items-center gap-1.5 text-sm text-slate-500 hover:text-slate-700 transition-colors"
            >
                <ArrowLeft className="h-4 w-4" />
                Back to Job
            </button>

            <h1 className="text-2xl font-semibold text-slate-900">Candidate Analysis</h1>

            <ScoreHeader arisScore={analysis.arisScore} vectorSimilarity={analysis.vectorSimilarity} />

            <section>
                <h2 className="text-lg font-semibold text-slate-900 mb-4">Five-Tier Breakdown</h2>
                <TierBreakdown
                    matchingSkills={analysis.matchingSkills}
                    implicitlyDiscoveredSkills={analysis.implicitlyDiscoveredSkills}
                    prerequisiteMetSkills={analysis.prerequisiteMetSkills}
                    bridgeableSkills={analysis.bridgeableSkills}
                    hardGaps={analysis.hardGaps}
                />
            </section>

            <section className="space-y-4 pt-4 border-t border-slate-100">
                <h2 className="text-lg font-semibold text-slate-900">AI Explanation</h2>
                {!summary ? (
                    <Button
                        onClick={() => summaryMutation.mutate()}
                        disabled={summaryMutation.isPending}
                        className="bg-slate-900 hover:bg-slate-800 text-white"
                    >
                        {summaryMutation.isPending && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                        Generate Candidate Explanation
                    </Button>
                ) : (
                    <Card>
                        <CardContent className="p-6 space-y-4">
                            <p className="text-sm leading-relaxed text-slate-700">{summary.text}</p>
                            <div className="flex items-center gap-2 text-xs text-slate-400">
                                <span>Graph Grounding Score: {summary.score.toFixed(2)}</span>
                                {summary.score >= 0.95 ? (
                                    <Badge variant="outline" className="border-green-400 text-green-700 bg-green-50">High Confidence</Badge>
                                ) : (
                                    <Badge variant="outline" className="border-yellow-400 text-yellow-700 bg-yellow-50">LLM Extrapolation Detected</Badge>
                                )}
                            </div>
                        </CardContent>
                    </Card>
                )}
            </section>
        </div>
    );
}
