using Polly;
using Polly.Extensions.Http;
using Polly.Timeout;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var fallbackPolicy = Policy<HttpResponseMessage>
    .Handle<TimeoutRejectedException>()
    .FallbackAsync(
        fallbackValue: new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(
                "O serviço está temporariamente indisponível. Por favor, tente novamente mais tarde.")
        },
        onFallbackAsync: (result, context) =>
        {
            Console.WriteLine("Falha ao obter a resposta. Aplicando fallback.");
            return Task.CompletedTask;
        });

// Criação de uma política de retry exponencial
IAsyncPolicy<HttpResponseMessage> retryPolicy = HttpPolicyExtensions
    .HandleTransientHttpError() // Lida com falhas transitórias, como 5xx e problemas de conectividade.
    .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
        (result, timeSpan, retryCount, context) =>
        {
            Console.WriteLine($"Tentativa {retryCount} falhou. Aguardando {timeSpan} antes da próxima tentativa.");
        });

// Criação de uma política de Circuit Breaker
IAsyncPolicy<HttpResponseMessage> circuitBreakerPolicy = HttpPolicyExtensions
    .HandleTransientHttpError()
    .CircuitBreakerAsync(5, TimeSpan.FromSeconds(30),
        onBreak: (result, breakDelay) =>
        {
            Console.WriteLine($"Circuito quebrado! Esperando {breakDelay.TotalSeconds} segundos antes de reabrir.");
        },
        onReset: () => { Console.WriteLine("Circuito reaberto, voltando a tentar."); });

var timeoutPolicy = Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(35));

builder.Services.AddHttpClient("MyHttpClient", client => { client.BaseAddress = new Uri("https://localhost:7132"); })
    .AddPolicyHandler(timeoutPolicy) // Timeout aplicado primeiro para limitar a duração total da requisição.
    .AddPolicyHandler(retryPolicy) // Retry aplicado após o timeout para lidar com falhas transitórias.
    .AddPolicyHandler(
        circuitBreakerPolicy) // Circuit breaker aplicado após o retry para evitar novas tentativas em caso de falhas consecutivas.
    .AddPolicyHandler(
        fallbackPolicy); // Fallback é aplicado por último para fornecer uma resposta amigável em caso de falha.

var app = builder.Build();

app.MapGet("/", async (IHttpClientFactory httpClientFactory, ILogger<Program> logger) =>
    {
        try
        {
            var client = httpClientFactory.CreateClient("MyHttpClient");
            var response = await client.GetAsync("/templates?page=1&pageSize=50");
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            return Results.Text(content);
        }
        catch (HttpRequestException httpEx)
        {
            logger.LogError(httpEx, "Erro de requisição ao obter templates");
            return Results.StatusCode(503);
        }
        catch (TimeoutRejectedException timeoutEx)
        {
            logger.LogError(timeoutEx, "Timeout ao obter templates");
            return Results.StatusCode(504);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro ao obter templates");
            return Results.Problem("Erro ao processar a requisição. Por favor, tente novamente mais tarde.");
        }
    })
    .Produces<string>()
    .WithName("GetTemplates")
    .WithTags("Templates")
    .WithOpenApi();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();


app.Run();