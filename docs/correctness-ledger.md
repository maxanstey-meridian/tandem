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
| Local capability acceptance | Validated capabilities execute once in process, transition typed state, and terminate the accepted turn through MAF middleware. | `LocalCapabilityTests`; `DebateCompositionTests.Debate_ExecutesRevisionLoopAndLocalCapabilityThroughPublicAuthoringSurface` | Closed |
| Capability failure isolation | Invalid, malformed, cancelled, conflicting, or failed acceptance performs no state transition and permits a valid retry where applicable. | `LocalCapabilityTests` validation, cancellation, concurrency, and acceptance-failure proofs | Closed |
| Process-owned runtime | `run` uses one in-process MAF runtime with no attach or scheduler path. | `ProjectBoundaryTests.Tool_UsesOneProcessOwnedRuntimeAndPersistsPublicationMetadata`; `InProcessPipelineRunnerTests` | Closed |
| Privileged Debate coupling | Debate is an external public-API consumer. | `ProjectBoundaryTests.Debate_IsAnUnprivilegedConsumerWithoutMafOrDeliveryVocabulary`; Debate in-process proofs | Closed |
| Public package surface | Minimal and Advanced exports are deliberate and contain no infrastructure or forbidden framework signatures. | `PublicApiBoundaryTests`; reviewed `ExportedApi.txt` manifests | Closed |
| Packed consumer execution | Songwriter, Support, and Debate restore and run from packages; generated stages receive the analyzer; dependency graphs remain isolated. | `PackageConsumerTests.PackedPackages_RunProgressiveSamplesWithIsolatedDependencies` | Closed |
| Session default and continuation | Agents start fresh unless conversation retention is explicitly selected. | `AgentSessionTests.SessionHistory_IsFreshByDefault_AndRetainedOnlyWhenExplicit` | Closed |
| Advanced profile selection | Profile policy selects the governing client before invocation. | `RegistrationTests.AdvancedProfilePolicy_SelectsClientBeforeTheGovernedInvocation` | Closed |

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
