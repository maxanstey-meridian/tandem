# Correctness Ledger

This ledger records the confirmed pre-refactor findings and the permanent proof
that closes each one.

| Finding | Intended invariant | Closing proof | Status |
| --- | --- | --- | --- |
| Planner and reviewer workspace access | Planner and reviewer inspect but cannot mutate. | `DeliveryPolicyRegressionTests.PlannerAndReviewer_AreReadOnlyRegardlessOfPipelineAuthority` | Closed |
| Executor mutation authority | Only an authorized executor mutates. | `Executor_WorkspaceMutationTracksEstablishedAuthority`; `ExecutorMutationGate_CoversEveryRegisteredWorkspaceMutationTool` | Closed |
| Role-specific checkpointing | Only the owning role receives checkpoint behavior. | `DeliveryPolicyRegressionTests.CheckpointPolicy_IsOwnedOnlyByExecutor` | Closed |
| Reviewer human-answer restoration | Reviewer answers restore and route back to reviewer. | `StructuredOutputTests.PersistedReviewerHumanAnswer_IsRestoredIntoPromptAndClearedAfterDecision`; `DeliveryCompositionGraphTests.ApplyHumanAnswer_HasBoth_ReturnRoutes` | Closed |
| Live human continuation | A process-owned run waits asynchronously, consumes the matching typed answer, and invokes the originating role. | `InProcessPipelineRunnerTests.RunAsync_ResumesTypedInteractionWithMatchingAnswer`; `WaitingRun_DoesNotPreventAnotherRunFromCompleting`; `InMemoryExternalRequestBrokerTests` | Closed |
| Lifecycle receipt replay | Accepted actions replay without another model call or MCP process. | `LifecycleMcpTests.ExistingSubmitReportReceipt_AppliesTransition_WithoutModelOrProcess` | Closed |
| MCP resource cleanup | Cancellation and replay leave no child process. | `LifecycleMcpTests.Cancellation_WhileMcpCallActive_FailsBlockAndLeavesNoChild`; `ExistingSubmitReportReceipt_AppliesTransition_WithoutModelOrProcess` | Closed |
| Process-owned runtime | `run` uses one in-process MAF runtime with no attach or scheduler path. | `ProjectBoundaryTests.Tool_UsesOneProcessOwnedRuntimeAndPersistsPublicationMetadata`; `InProcessPipelineRunnerTests` | Closed |
| Privileged Debate coupling | Debate is an external public-API consumer. | `ProjectBoundaryTests.Debate_IsAnUnprivilegedConsumerWithoutMafOrDeliveryVocabulary`; Debate in-process proofs | Closed |

## Framework Characterization

These pinned MAF behaviors constrain Tandem's authoring adapter:

| Finding | Constraint | Existing or target proof | Owner |
| --- | --- | --- | --- |
| Same-target routing | Multiple matching edges to one target must not create duplicate physical deliveries for one logical route. | Generated authoring and composition route proofs. | Tandem authoring |
| Ordered conditional routing | In-process execution selects the first matching conditional route. | Generated authoring and composition route proofs. | MAF/Tandem adapter |
| Observation capability preservation | Observation must preserve semantic identity, typing, and supported execution capabilities. | Generated-adapter and envelope invariant proofs. | Tandem adapter |
| Interaction identity | Live request and answer correlation uses both run and request identity. | `InProcessPipelineRunnerTests.RunAsync_RejectsAnswerForAnotherRequest`; `InMemoryExternalRequestBrokerTests.Answer_RejectsWrongDuplicateAndLateAnswers` | Tandem runtime |

All findings remain backed by tests at their owning boundary rather than
helper-only unit tests.
