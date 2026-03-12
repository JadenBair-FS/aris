import { apiClient } from './client';
import type { MatchAnalysisResult, MatchSummaryResult, RecruiterSummaryResult } from '../types/api';

export const matchApi = {
    analyze: (userProfileId: string, jobId: string) => {
        return apiClient<MatchAnalysisResult>('/match/analyze', {
            method: 'POST',
            body: JSON.stringify({ userProfileId, jobId }),
        });
    },
    analyzeQuick: (userProfileId: string, jobDescriptionText: string): Promise<MatchAnalysisResult> => {
        return apiClient<MatchAnalysisResult>('/match/analyze-quick', {
            method: 'POST',
            body: JSON.stringify({ userProfileId, jobDescriptionText }),
        });
    },
    getSummary: (userProfileId: string, jobId: string) => {
        return apiClient<MatchSummaryResult>('/match/summary', {
            method: 'POST',
            body: JSON.stringify({ userProfileId, jobId }),
        });
    },
    getRecruiterSummary: (userProfileId: string, jobId: string) => {
        return apiClient<RecruiterSummaryResult>('/match/recruiter-summary', {
            method: 'POST',
            body: JSON.stringify({ userProfileId, jobId }),
        });
    },
    getFastScoresCandidates: (jobId: string, limit = 20) =>
        apiClient<{ jobId: string; scores: { userProfileId: string; userId: string; primaryRole: string; fastArisScore: number }[] }>(
            `/match/scores/candidates/${jobId}?limit=${limit}`
        ),
    getFastScoresJobs: (profileId: string, limit = 20) =>
        apiClient<{ profileId: string; scores: { jobId: string; title: string; fastArisScore: number }[] }>(
            `/match/scores/jobs/${profileId}?limit=${limit}`
        ),
    explain: (userProfileId: string, jobId: string) =>
        apiClient<{ explanation: string }>('/match/explain', {
            method: 'POST',
            body: JSON.stringify({ userProfileId, jobId }),
        }),
};
