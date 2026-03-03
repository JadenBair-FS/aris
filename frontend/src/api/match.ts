import { apiClient } from './client';
import type { MatchAnalysisResult, MatchSummaryResult } from '../types/api';

export const matchApi = {
    analyze: (userProfileId: string, jobId: string) => {
        return apiClient<MatchAnalysisResult>('/match/analyze', {
            method: 'POST',
            body: JSON.stringify({ userProfileId, jobId }),
        });
    },
    getSummary: (userProfileId: string, jobId: string) => {
        return apiClient<MatchSummaryResult>('/match/summary', {
            method: 'POST',
            body: JSON.stringify({ userProfileId, jobId }),
        });
    }
};
