namespace FCG.Users.Api;

public static class ContainerProbe
{
    public static async Task<int> RunAsync(Uri endpoint, TimeSpan? timeout = null)
    {
        using var client = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
    }
}
