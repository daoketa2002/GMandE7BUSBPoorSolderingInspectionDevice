using System.Runtime.CompilerServices;

// 仅允许最小回归测试访问阶段 C 的确定性通信测试钩子。
[assembly: InternalsVisibleTo("MinimumLoopTests")]
