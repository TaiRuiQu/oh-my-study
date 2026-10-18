namespace OhMyStudy.Domain;

/// <summary>P1 only establishes dependency direction. Business contracts are ported in P2.</summary>
public enum MigrationStage { Baseline, TechnicalProof, Domain, Storage, Execution, Grading, Documents, Workflows, Experience, Release }
