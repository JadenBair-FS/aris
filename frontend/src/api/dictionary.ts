import { apiClient } from './client';
import type { SkillCandidate } from '../types/api';

export const dictionaryApi = {
    getSkillCandidates: (name: string, limit = 10) =>
        apiClient<SkillCandidate[]>(
            `/dictionary/skill-candidates?name=${encodeURIComponent(name)}&limit=${limit}`
        ),
};
