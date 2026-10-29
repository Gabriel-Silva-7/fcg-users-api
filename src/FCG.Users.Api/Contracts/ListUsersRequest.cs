using System.ComponentModel.DataAnnotations;
using FCG.Users.Application.Identity;
using Microsoft.AspNetCore.Mvc;

namespace FCG.Users.Api.Contracts;

public sealed record ListUsersRequest
{
    [FromQuery(Name = "page")]
    [Range(ListUsersQuery.MinPage, ListUsersQuery.MaxPage)]
    public int Page { get; init; } = ListUsersQuery.MinPage;

    [FromQuery(Name = "pageSize")]
    [Range(ListUsersQuery.MinPageSize, ListUsersQuery.MaxPageSize)]
    public int PageSize { get; init; } = ListUsersQuery.DefaultPageSize;

    [FromQuery(Name = "search")]
    [StringLength(ListUsersQuery.MaxSearchLength)]
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Search { get; init; }
}
