import { Info } from 'lucide-react';
import { Card, CardContent } from './ui/card';
import { Badge } from './ui/badge';
import { formatArisScore } from '@/utils/score';

export function ScoreHeader({ arisScore, vectorSimilarity }: { arisScore: number; vectorSimilarity: number }) {
    const scoreColorClass =
        arisScore >= 0.65 ? 'text-green-600' :
        arisScore >= 0.40 ? 'text-orange-500' :
        'text-red-600';
    const scoreBadgeClass =
        arisScore >= 0.65 ? 'bg-green-50 text-green-700 border-green-200' :
        arisScore >= 0.40 ? 'bg-orange-50 text-orange-700 border-orange-200' :
        'bg-red-50 text-red-700 border-red-200';
    const scoreBadgeLabel =
        arisScore >= 0.65 ? 'Well Qualified' :
        arisScore >= 0.40 ? 'Partial Match' :
        'Significant Gaps';

    return (
        <Card className="mb-6">
            <CardContent className="flex items-center justify-between p-6">
                <div className="flex flex-col gap-1">
                    <div className="flex items-center gap-2">
                        <span className="text-sm font-medium text-muted-foreground uppercase tracking-wider">ARIS Score</span>
                        <div className="group relative">
                            <Info className="h-4 w-4 text-muted-foreground" />
                            <div className="absolute bottom-full mb-2 hidden group-hover:block w-64 bg-popover text-popover-foreground text-xs rounded p-2 shadow-md z-10 border">
                                ArisScore = 0.40 × VectorSimilarity + 0.60 × GraphCoverageScore.
                            </div>
                        </div>
                    </div>
                    <div className="flex items-center gap-2">
                        <span className={`text-4xl font-bold ${scoreColorClass}`}>{formatArisScore(arisScore)}</span>
                        <Badge variant="outline" className={`text-xs ${scoreBadgeClass}`}>
                            {scoreBadgeLabel}
                        </Badge>
                    </div>
                </div>

                <div className="flex flex-col text-right">
                    <span className="text-sm font-medium text-muted-foreground uppercase tracking-wider">Embedding Match</span>
                    <span className="text-2xl font-semibold text-muted-foreground">{vectorSimilarity.toFixed(2)}</span>
                </div>
            </CardContent>
        </Card>
    );
}
