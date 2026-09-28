import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/card';
import { ApiError } from '../../lib/api/client';
import { testcaseKeys, testcasesEndpoints, type CreateTestCaseInput } from '../../lib/api/endpoints/testcases';
import { TestCaseForm } from './TestCaseForm';

export function TestCaseNewPage() {
  const { projectId = '' } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [serverError, setServerError] = useState<ApiError | null>(null);

  const create = useMutation({
    mutationFn: (input: CreateTestCaseInput) => testcasesEndpoints.create(projectId, input),
    onSuccess: (testCase) => {
      void queryClient.invalidateQueries({ queryKey: testcaseKeys.all });
      navigate(`/projects/${projectId}/test-cases/${testCase.id}`);
    },
    onError: (error: ApiError) => setServerError(error),
  });

  return (
    <div className="mx-auto max-w-4xl space-y-6">
      <div>
        <Link to={`/projects/${projectId}/test-cases`} className="text-sm text-brand-700 hover:text-brand-600">
          ← Back to test cases
        </Link>
        <h1 className="mt-2 text-xl font-semibold text-slate-900">New test case</h1>
        <p className="mt-1 text-sm text-slate-500">
          Creates the logical test case together with immutable version 1.
        </p>
      </div>
      <Card>
        <CardHeader>
          <CardTitle>Test case</CardTitle>
        </CardHeader>
        <CardContent>
          <TestCaseForm
            submitLabel="Create test case"
            submitting={create.isPending}
            serverError={serverError}
            onSubmit={(values) => {
              setServerError(null);
              create.mutate({
                testKey: values.testKey,
                title: values.title,
                description: values.description || undefined,
                module: values.module || undefined,
                framework: values.framework || undefined,
                platform: values.platform || undefined,
                priority: values.priority,
                status: values.status,
                sourceType: values.sourceType,
                sourceCode: values.sourceCode || undefined,
                structuredSteps:
                  values.structuredSteps.length > 0
                    ? values.structuredSteps.map((s, i) => ({
                        order: i + 1,
                        action: s.action,
                        target: s.target || null,
                        value: s.value || null,
                      }))
                    : undefined,
              });
            }}
          />
        </CardContent>
      </Card>
    </div>
  );
}
