import { Badge } from './ui/badge';

export function SkillBadge({ importance }: { importance: string }) {
    const isEssential = importance?.toLowerCase() === 'essential';
    return (
        <Badge variant={isEssential ? 'default' : 'secondary'} className="ml-2">
            {importance}
        </Badge>
    );
}
