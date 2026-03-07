export interface SkillGapItem {
    skillName: string;
    importance: 'Essential' | 'Preferred' | string;
    yearsRequired: number;
    candidateYears: number;
    bridgePath: string | null;
    bridgeSource: string | null;
}

export interface UngroundedSkillComparison {
    matched: string[];
    missingFromResume: string[];
    extraInResume: string[];
}

export interface MatchAnalysisResult {
    jobId: string;
    vectorSimilarity: number;
    arisScore: number;
    matchingSkills: SkillGapItem[];
    implicitlyDiscoveredSkills: string[];
    prerequisiteMetSkills: SkillGapItem[];
    bridgeableSkills: SkillGapItem[];
    hardGaps: SkillGapItem[];
    ungroundedComparison: UngroundedSkillComparison;
}

export interface MatchSummaryResult {
    summary: string;
    groundingScore: number;
}

export interface TailoredBullet {
    originalBullet: string;
    rewrittenBullet: string;
    targetSkill: string;
    role: string;
    company: string;
    bridgePath: string | null;
}

export interface RecruiterSummaryResult {
    summary: string;
    groundingScore: number;
    verdict: 'Strong Fit' | 'Potential Fit' | 'Not Recommended' | string;
}

// Resume CleanSignal — matches ResumeCleanSignal.cs (snake_case from JSON)
export interface CleanSignalSkill {
    name: string;
    category: string;
    proficiency: string;
    years_of_experience: number;
}

export interface CleanSignalRole {
    title: string;
    duration: string;
    is_current: boolean;
}

export interface ExperienceSummaryEntry {
    role: string;
    company: string;
    bullets: string[];
}

export interface EducationEntry {
    degree: string;
    institution: string;
    year: string;
}

export interface ResumeCleanSignal {
    roles: CleanSignalRole[];
    skills: CleanSignalSkill[];
    experience_summary: ExperienceSummaryEntry[];
    education: EducationEntry[];
    ungrounded_skills: CleanSignalSkill[];
}

export interface UserProfileDetail {
    id: string;
    userId: string;
    cleanSignal: ResumeCleanSignal | null;
    rawResume: string;
    createdAt: string;
    hasResume?: boolean;
}

// Job CleanSignal — matches JobPostingCleanSignal.cs (snake_case from JSON)
export interface JobRole {
    title: string;
    priority: string;
}

export interface JobRequiredSkill {
    name: string;
    importance: 'Essential' | 'Preferred' | string;
    years_of_experience: number;
}

export interface JobEducation {
    degree: string;
    required: string;
}

export interface JobCleanSignal {
    target_roles: JobRole[];
    required_skills: JobRequiredSkill[];
    responsibilities: string[];
    minimum_education: JobEducation[];
    ungrounded_skills: JobRequiredSkill[];
}

export interface JobPostingDetail {
    id: string;
    recruiterId: string;
    rawDescription: string;
    cleanSignal: JobCleanSignal | null;
    sourceUrl?: string | null;
    createdAt?: string;
    updatedAt?: string;
}

export interface JobMatchResult {
    jobId: string;
    score: number;
    distance: number;
    arisScore: number;
    job: JobPostingDetail | null;
}

export interface JobRecommendationResponse {
    matches: JobMatchResult[];
    analysis: string;
}

export interface CandidateResult {
    userProfileId: string;
    userId: string;
    primaryRole: string;
    vectorSimilarity: number;
}

export interface CandidateSearchResponse {
    jobId: string;
    candidates: CandidateResult[];
}
