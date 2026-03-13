import { Navigate, Link, useNavigate } from 'react-router';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { resumeApi } from '@/api/resume';
import { useAuth } from '@/context/AuthContext';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { SkillBadge } from '@/components/SkillBadge';

export default function Profile() {
    const { user } = useAuth();
    const navigate = useNavigate();
    const queryClient = useQueryClient();

    const { data: profile, isLoading, isError } = useQuery({
        queryKey: ['profileByUser', user?.id],
        queryFn: () => resumeApi.getProfileByUserId(user!.id),
        enabled: !!user,
        retry: false,
    });

    const deleteResumeMutation = useMutation({
        mutationFn: resumeApi.deleteResume,
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['profileByUser'] });
            navigate('/profile/upload');
        },
    });

    if (isLoading) {
        return (
            <div className="space-y-4">
                <Skeleton className="h-24 w-full" />
                <Skeleton className="h-20 w-full" />
                <div className="grid grid-cols-2 gap-4">
                    <Skeleton className="h-60" />
                    <Skeleton className="h-60" />
                </div>
            </div>
        );
    }

    if (isError || !profile || !profile.hasResume) {
        return <Navigate to="/profile/upload" replace />;
    }

    const signal = profile.cleanSignal!;
    const currentRole = signal.roles.find(r => r.is_current) ?? signal.roles[0];

    const skillsByCategory = signal.skills.reduce<Record<string, typeof signal.skills>>((acc, skill) => {
        const cat = skill.category || 'Other';
        if (!acc[cat]) acc[cat] = [];
        acc[cat].push(skill);
        return acc;
    }, {});

    return (
        <div className="space-y-4">
            {/* Title — text left, button right */}
            <Card>
                <CardContent className="px-6 py-5 flex items-center justify-between gap-4">
                    <div>
                        <h1 className="text-xl font-semibold text-slate-900">{currentRole?.title ?? 'No Role'}</h1>
                        <p className="text-slate-500 text-sm mt-0.5">{user?.name}</p>
                    </div>
                    <div className="flex gap-2 shrink-0">
                        <Button asChild variant="outline" size="sm">
                            <Link to="/profile/upload">Update Resume</Link>
                        </Button>
                        <Button
                            variant="outline"
                            size="sm"
                            className="text-red-600 hover:text-red-700 border-red-200 hover:border-red-300"
                            disabled={deleteResumeMutation.isPending}
                            onClick={() => {
                                if (window.confirm('Remove your resume? This cannot be undone.')) {
                                    deleteResumeMutation.mutate();
                                }
                            }}
                        >
                            Remove Resume
                        </Button>
                    </div>
                </CardContent>
            </Card>

            {/* Two-column: Experience + Education (left) | Skills (right) */}
            <div className="grid grid-cols-1 lg:grid-cols-2 gap-4 items-start">
                {/* Left column: Experience then Education */}
                <div className="space-y-4">
                    {signal.experience_summary.length > 0 && (
                        <Card>
                            <CardHeader className="pb-2 pt-4 px-5">
                                <CardTitle className="text-sm font-semibold text-slate-500 uppercase tracking-wider">Experience</CardTitle>
                            </CardHeader>
                            <CardContent className="px-5 pb-4 space-y-5">
                                {signal.experience_summary.map((exp, i) => (
                                    <div key={i} className={i > 0 ? 'border-t border-slate-100 pt-4' : ''}>
                                        <p className="font-medium text-slate-900 text-sm">{exp.company}</p>
                                        <p className="text-sm text-slate-500 mb-2">{exp.role}</p>
                                        {exp.bullets.length > 0 && (
                                            <ul className="list-disc list-inside space-y-1">
                                                {exp.bullets.map((b, j) => (
                                                    <li key={j} className="text-sm text-slate-600">{b}</li>
                                                ))}
                                            </ul>
                                        )}
                                    </div>
                                ))}
                            </CardContent>
                        </Card>
                    )}

                    {signal.education.length > 0 && (
                        <Card>
                            <CardHeader className="pb-2 pt-4 px-5">
                                <CardTitle className="text-sm font-semibold text-slate-500 uppercase tracking-wider">Education</CardTitle>
                            </CardHeader>
                            <CardContent className="px-5 pb-4 space-y-3">
                                {signal.education.map((edu, i) => (
                                    <div key={i} className="flex justify-between items-start">
                                        <div>
                                            <p className="text-sm font-medium text-slate-900">{edu.degree}</p>
                                            <p className="text-sm text-slate-500">{edu.institution}</p>
                                        </div>
                                        {edu.year && <p className="text-sm text-slate-400 shrink-0">{edu.year}</p>}
                                    </div>
                                ))}
                            </CardContent>
                        </Card>
                    )}
                </div>

                {/* Right column: Skills */}
                {(Object.keys(skillsByCategory).length > 0 || signal.ungrounded_skills?.length > 0) && (
                    <Card>
                        <CardHeader className="pb-2 pt-4 px-5">
                            <CardTitle className="text-sm font-semibold text-slate-500 uppercase tracking-wider">Skills</CardTitle>
                        </CardHeader>
                        <CardContent className="px-5 pb-4 space-y-4">
                            {Object.entries(skillsByCategory).map(([category, skills]) => (
                                <div key={category}>
                                    <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-2">{category}</p>
                                    <div className="flex flex-wrap gap-1.5">
                                        {skills.map((skill, i) => (
                                            <SkillBadge
                                                key={i}
                                                name={skill.name}
                                                originalName={skill.original_name}
                                                yearsOfExperience={skill.years_of_experience}
                                            />
                                        ))}
                                    </div>
                                </div>
                            ))}
                            {signal.ungrounded_skills?.length > 0 && (
                                <div>
                                    <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-2">Not in Database</p>
                                    <div className="flex flex-wrap gap-1.5">
                                        {signal.ungrounded_skills.map((skill, i) => (
                                            <SkillBadge
                                                key={i}
                                                name={skill.name}
                                                originalName={skill.original_name}
                                                yearsOfExperience={skill.years_of_experience}
                                                className="border border-dashed border-slate-300 bg-transparent text-slate-500"
                                            />
                                        ))}
                                    </div>
                                </div>
                            )}
                        </CardContent>
                    </Card>
                )}
            </div>
        </div>
    );
}
