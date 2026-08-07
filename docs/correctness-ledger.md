# Correctness Ledger

This ledger records the confirmed pre-refactor findings and the permanent proof
that closes each one.

| Finding | Intended invariant | Closing proof | Status |
| --- | --- | --- | --- |
| Planner and reviewer workspace access | Planner and reviewer inspect but cannot mutate. | `DeliveryPolicyRegressionTests.PlannerAndReviewer_AreReadOnlyRegardlessOfPipelineAuthority` | Closed |
| Executor mutation authority | Only an authorized executor mutates. | `Executor_WorkspaceMutationTracksEstablishedAuthority`; `ExecutorMutationGate_CoversEveryRegisteredWorkspaceMutationTool` | Closed |
| Role-specific checkpointing | Only the owning role receives checkpoint behavior. | `DeliveryPolicyRegressionTests.CheckpointPolicy_IsOwnedOnlyByExecutor` | Closed |
| Reviewer human-answer restoration | Reviewer answers restore and route back to reviewer. | `StructuredOutputTests.PersistedReviewerHumanAnswer_IsRestoredIntoPromptAndClearedAfterDecision`; `DeliveryCompositionGraphTests.ApplyHumanAnswer_HasBoth_ReturnRoutes` | Closed |
| Durable human resume | A durable run suspends, consumes a typed answer, and invokes the originating role. | `HumanSuspensionProofTests.PlannerNeedsHuman_SuspendsAndResumes_WithAnswer`; `FitGateDTests.RequestPort_SurvivesHostRestartAndConsumesTypedResponse` | Closed |
| Lifecycle receipt replay | Accepted actions replay without another model call or MCP process. | `LifecycleMcpTests.ExistingSubmitReportReceipt_AppliesTransition_WithoutModelOrProcess` | Closed |
| MCP resource cleanup | Cancellation and replay leave no child process. | `LifecycleMcpTests.Cancellation_WhileMcpCallActive_FailsBlockAndLeavesNoChild`; `ExistingSubmitReportReceipt_AppliesTransition_WithoutModelOrProcess` | Closed |
| Attach metadata publication | Metadata is durable before a run ID is attachable and attach selects by identity. | `ProjectBoundaryTests.Tool_PersistsCompositionMetadataBeforePublishingAnAttachableRunId`; `ProjectionTests.WriteAndRead_RoundTripsProjection` | Closed |
| Privileged Debate coupling | Debate is an external public-API consumer. | `ProjectBoundaryTests.Debate_IsAnUnprivilegedConsumerWithoutMafOrDeliveryVocabulary`; Debate in-process and durable proofs | Closed |

## Framework Characterization

These pinned MAF behaviors constrain Tandem's authoring adapter:

| Finding | Constraint | Existing or target proof | Owner |
| --- | --- | --- | --- |
| Same-target durable batching | Multiple matching edges to one target can deliver a batched payload rather than one typed message. Tandem must not create duplicate physical same-target deliveries for one logical route. | Existing combined-edge production workaround; add route-adapter durable proof. | Slice 1 |
| `AddSwitch` runtime divergence | In-process execution selects the first matching case while the pinned durable adapter executes every matching case. Tandem must not promise ordered `if/else` semantics. | `FitGateBTests.AddSwitch_InProcessExecutesOnlyTheFirstMatchingCase` and `AddSwitch_DurablePinnedPreviewRunsEveryMatchingCase` | Slice 1 |
| Observation capability preservation | Observation must preserve semantic identity, typing, durability, and supported execution capabilities. | Slice 0 plain-step adapter characterization; permanent generated-adapter proof in Slice 1. | Slices 0-1 |
| Durable semantic identity | Rebuilt adapters and bindings with stable workflow, step, and port identities resume without relying on binding object identity. | Existing restart and request-port proofs; permanent generated-adapter restart proof in Slice 1. | Slices 0-1 |

All Slice 0 findings are closed. Durable, process, host-wiring, and package
findings remain backed by tests at their owning boundary rather than helper-only
unit tests.
