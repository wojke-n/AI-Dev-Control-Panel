using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiDevControlPanel.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    LocalPath = table.Column<string>(type: "TEXT", nullable: false),
                    GitHubRepo = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultBranch = table.Column<string>(type: "TEXT", nullable: false),
                    IsPublic = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsChecked = table.Column<bool>(type: "INTEGER", nullable: false),
                    OnboardingState = table.Column<string>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tsas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: true),
                    RunInProgress = table.Column<bool>(type: "INTEGER", nullable: false),
                    PendingQuestions = table.Column<string>(type: "TEXT", nullable: true),
                    PendingAnswer = table.Column<string>(type: "TEXT", nullable: true),
                    PendingRefinement = table.Column<string>(type: "TEXT", nullable: true),
                    AwaitingSecurityConfirmation = table.Column<bool>(type: "INTEGER", nullable: false),
                    ResumeAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tsas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tsas_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Issues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    TsaId = table.Column<int>(type: "INTEGER", nullable: true),
                    ParentIssueId = table.Column<int>(type: "INTEGER", nullable: true),
                    RefinerRef = table.Column<string>(type: "TEXT", nullable: true),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    GitHubId = table.Column<long>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    IsContainer = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsPrioritized = table.Column<bool>(type: "INTEGER", nullable: false),
                    OnHold = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: true),
                    RunInProgress = table.Column<bool>(type: "INTEGER", nullable: false),
                    CurrentPlan = table.Column<string>(type: "TEXT", nullable: true),
                    PlanIteration = table.Column<int>(type: "INTEGER", nullable: false),
                    LastReport = table.Column<string>(type: "TEXT", nullable: true),
                    PendingQuestions = table.Column<string>(type: "TEXT", nullable: true),
                    PendingAnswer = table.Column<string>(type: "TEXT", nullable: true),
                    PendingFeedback = table.Column<string>(type: "TEXT", nullable: true),
                    Branch = table.Column<string>(type: "TEXT", nullable: true),
                    WorktreePath = table.Column<string>(type: "TEXT", nullable: true),
                    IsInterrupted = table.Column<bool>(type: "INTEGER", nullable: false),
                    ResumeAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ErrorPhase = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    Notice = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Issues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Issues_Issues_ParentIssueId",
                        column: x => x.ParentIssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Issues_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Issues_Tsas_TsaId",
                        column: x => x.TsaId,
                        principalTable: "Tsas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "IssueDependencies",
                columns: table => new
                {
                    IssueId = table.Column<int>(type: "INTEGER", nullable: false),
                    BlockedByIssueId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueDependencies", x => new { x.IssueId, x.BlockedByIssueId });
                    table.ForeignKey(
                        name: "FK_IssueDependencies_Issues_BlockedByIssueId",
                        column: x => x.BlockedByIssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IssueDependencies_Issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IssueDependencies_BlockedByIssueId",
                table: "IssueDependencies",
                column: "BlockedByIssueId");

            migrationBuilder.CreateIndex(
                name: "IX_Issues_ParentIssueId",
                table: "Issues",
                column: "ParentIssueId");

            migrationBuilder.CreateIndex(
                name: "IX_Issues_ProjectId_Number",
                table: "Issues",
                columns: new[] { "ProjectId", "Number" });

            migrationBuilder.CreateIndex(
                name: "IX_Issues_TsaId",
                table: "Issues",
                column: "TsaId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_LocalPath",
                table: "Projects",
                column: "LocalPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tsas_ProjectId",
                table: "Tsas",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IssueDependencies");

            migrationBuilder.DropTable(
                name: "Issues");

            migrationBuilder.DropTable(
                name: "Tsas");

            migrationBuilder.DropTable(
                name: "Projects");
        }
    }
}
