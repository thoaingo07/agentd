// Classes share one PostgreSQL container but each gets its own database, so class-level parallelism is safe.
[assembly: Parallelize(Scope = ExecutionScope.ClassLevel)]
