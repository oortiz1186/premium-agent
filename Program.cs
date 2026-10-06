using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "CRM MIDA Premium Agent");
builder.Services.Configure<PremiumAgentOptions>(builder.Configuration.GetSection("PremiumAgent"));
builder.Services.AddSingleton<PremiumSdkGateway>();

var app = builder.Build();
var premiumSdk = app.Services.GetRequiredService<PremiumSdkGateway>();

// Abre una sola sesión del SDK al iniciar el agente. Todas las peticiones posteriores
// reutilizan esta misma sesión y el mismo hilo dedicado.
await premiumSdk.StartAsync(CancellationToken.None);
app.Lifetime.ApplicationStopping.Register(premiumSdk.Dispose);

app.MapGet("/health", (PremiumSdkGateway sdk) =>
{
    var result = sdk.GetHealth();
    return result.Ok ? Results.Ok(result) : Results.Json(result, statusCode: 503);
});

var api = app.MapGroup("/api");
api.AddEndpointFilter(async (context, next) =>
{
    var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
    var expected = configuration["PremiumAgent:ApiKey"] ?? string.Empty;
    var supplied = context.HttpContext.Request.Headers["X-CRM-MIDA-Key"].FirstOrDefault() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(expected) || expected == "CAMBIAR-ESTA-CLAVE")
        return Results.Json(new { message = "PremiumAgent:ApiKey no está configurada de forma segura." }, statusCode: 503);
    if (!SecureEquals(expected, supplied))
        return Results.Unauthorized();
    return await next(context);
});

api.MapGet("/customers/by-code/{code}", async (string code, PremiumSdkGateway sdk, CancellationToken ct) =>
{
    var customer = await sdk.FindByCodeAsync(code, ct);
    return customer is null ? Results.NotFound() : Results.Ok(customer);
});

api.MapGet("/customers/by-rfc/{rfc}", async (string rfc, PremiumSdkGateway sdk, CancellationToken ct) =>
{
    var customer = await sdk.FindByRfcAsync(rfc, ct);
    return customer is null ? Results.NotFound() : Results.Ok(customer);
});

api.MapPost("/customers", async (CreateCustomerRequest request, PremiumSdkGateway sdk, CancellationToken ct) =>
{
    try
    {
        var created = await sdk.CreateCustomerAsync(request, ct);
        return Results.Created($"/api/customers/by-code/{Uri.EscapeDataString(created.Code)}", created);
    }
    catch (PremiumConflictException ex)
    {
        return Results.Conflict(new { message = ex.Message, code = ex.Code });
    }
    catch (PremiumValidationException ex)
    {
        return Results.BadRequest(new { message = ex.Message, code = ex.Code });
    }
    catch (PremiumSdkException ex)
    {
        return Results.Json(new { message = ex.Message, code = ex.Code, sdkError = ex.SdkError }, statusCode: 502);
    }
});


api.MapPost("/quotes", async (CreateQuoteRequest request, PremiumSdkGateway sdk, CancellationToken ct) =>
{
    try
    {
        var created = await sdk.CreateQuoteAsync(request, ct);
        return Results.Created($"/api/quotes/{Uri.EscapeDataString(created.Series)}/{created.Folio}", created);
    }
    catch (PremiumValidationException ex)
    {
        return Results.BadRequest(new { message = ex.Message, code = ex.Code });
    }
    catch (PremiumSdkException ex)
    {
        return Results.Json(new { message = ex.Message, code = ex.Code, sdkError = ex.SdkError }, statusCode: 502);
    }
});

api.MapPut("/quotes/{documentId:int}", async (int documentId, UpdateQuoteRequest request, PremiumSdkGateway sdk, CancellationToken ct) =>
{
    try
    {
        var updated = await sdk.UpdateQuoteAsync(documentId, request, ct);
        return Results.Ok(updated);
    }
    catch (PremiumValidationException ex)
    {
        return Results.BadRequest(new { message = ex.Message, code = ex.Code });
    }
    catch (PremiumSdkException ex)
    {
        return Results.Json(new { message = ex.Message, code = ex.Code, sdkError = ex.SdkError }, statusCode: 502);
    }
});

app.Run();

static bool SecureEquals(string expected, string supplied)
{
    var a = Encoding.UTF8.GetBytes(expected);
    var b = Encoding.UTF8.GetBytes(supplied);
    return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
}

public sealed class PremiumAgentOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string SdkDirectory { get; set; } = @"C:\Program Files (x86)\Compac\COMERCIAL";
    public string CompanyDirectory { get; set; } = @"C:\Compac\Empresas\adMIDA_PRUEBAS";
    public string AllowedCompanyDirectoryName { get; set; } = "adMIDA_PRUEBAS";
    public string PriceList { get; set; } = "1";
    public string QuoteConceptCode { get; set; } = "1";
    public string QuoteSeries { get; set; } = "CZM";
    public string DefaultWarehouse { get; set; } = "1";
    public bool AllowWrites { get; set; }
}

public sealed record CreateCustomerRequest(
    string? Code,
    string BusinessName,
    string? TradeName,
    string Rfc,
    string? Email,
    string? Phone,
    string? ContactName);

public sealed record CreateQuoteRequest(
    string CustomerCode,
    string? CustomerRfc,
    int ValidityDays,
    string? Observations,
    IReadOnlyCollection<CreateQuoteItemRequest> Items);

public sealed record CreateQuoteItemRequest(
    string ProductCode,
    string? Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal TaxRate,
    int? PremiumMovementId = null);

public sealed record UpdateQuoteRequest(
    int ValidityDays,
    string? Observations,
    IReadOnlyCollection<CreateQuoteItemRequest> Items);

public sealed record QuoteMovementResult(int Index, int MovementId);

public sealed record QuoteResult(
    int DocumentId,
    Guid? DocumentGuid,
    string Series,
    decimal Folio,
    decimal Total,
    IReadOnlyCollection<QuoteMovementResult> Movements);

public sealed record UpdateQuoteResult(
    bool Ok,
    int DocumentId,
    IReadOnlyCollection<QuoteMovementResult> Movements);

public sealed record CustomerResult(
    string Code,
    string BusinessName,
    string? TradeName,
    string Rfc,
    string? Email,
    string? Phone,
    string? ContactName,
    string? PriceList);

public sealed record HealthResult(
    bool Ok,
    string Message,
    string SdkVersion,
    string Company,
    bool WritesEnabled,
    bool PersistentSession,
    int SdkThreadId);

public sealed class PremiumSdkGateway : IDisposable
{
    private readonly PremiumAgentOptions _options;
    private readonly ILogger<PremiumSdkGateway> _logger;
    private readonly BlockingCollection<SdkWorkItem> _queue = new();
    private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _sdkThread;
    private volatile bool _sessionReady;
    private volatile bool _disposed;
    private string _sdkVersion = "desconocida";
    private string? _lastError;
    private int _sdkThreadId;

    public PremiumSdkGateway(Microsoft.Extensions.Options.IOptions<PremiumAgentOptions> options, ILogger<PremiumSdkGateway> logger)
    {
        _options = options.Value;
        _logger = logger;
        _sdkThread = new Thread(SdkThreadMain)
        {
            IsBackground = true,
            Name = "CRM MIDA - CONTPAQi SDK"
        };
        _sdkThread.SetApartmentState(ApartmentState.STA);
        _sdkThread.Start();
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _started.Task.WaitAsync(ct);
        if (!_sessionReady)
            throw new InvalidOperationException(_lastError ?? "No fue posible iniciar la sesión persistente de CONTPAQi.");
    }

    public HealthResult GetHealth()
    {
        var company = Path.GetFileName(_options.CompanyDirectory.TrimEnd('\\', '/'));
        if (_sessionReady)
        {
            return new HealthResult(
                true,
                "Sesión persistente del SDK activa y empresa abierta.",
                _sdkVersion,
                company,
                _options.AllowWrites,
                true,
                _sdkThreadId);
        }

        return new HealthResult(
            false,
            _lastError ?? "La sesión del SDK no está activa.",
            _sdkVersion,
            company,
            _options.AllowWrites,
            true,
            _sdkThreadId);
    }

    public Task<CustomerResult?> FindByCodeAsync(string code, CancellationToken ct) =>
        ExecuteAsync(() => FindByCode(code), ct);

    public Task<CustomerResult?> FindByRfcAsync(string rfc, CancellationToken ct) =>
        ExecuteAsync(() => FindByRfc(rfc), ct);

    public Task<CustomerResult> CreateCustomerAsync(CreateCustomerRequest request, CancellationToken ct) => ExecuteAsync(() =>
    {
        EnsureWritesAllowed();
        var rfc = NormalizeRfc(request.Rfc);
        var businessName = (request.BusinessName ?? string.Empty).Trim();
        var tradeName = string.IsNullOrWhiteSpace(request.TradeName) ? businessName : request.TradeName.Trim();
        var code = NormalizeCode(string.IsNullOrWhiteSpace(request.Code) ? BuildCode(rfc) : request.Code);

        if (string.IsNullOrWhiteSpace(rfc)) throw new PremiumValidationException("validation_rfc", "El RFC es obligatorio.");
        if (string.IsNullOrWhiteSpace(businessName)) throw new PremiumValidationException("validation_business_name", "La razón social es obligatoria.");
        if (businessName.Length > 60) throw new PremiumValidationException("validation_business_name_length", "La razón social no puede exceder 60 caracteres para Comercial Premium.");
        if (code.Length > 30) throw new PremiumValidationException("validation_code_length", "El código no puede exceder 30 caracteres para Comercial Premium.");

        var duplicateCode = FindByCode(code);
        if (duplicateCode is not null) throw new PremiumConflictException("duplicate_code", $"Ya existe el cliente {code} en Comercial Premium.");
        var duplicateRfc = FindByRfc(rfc);
        if (duplicateRfc is not null) throw new PremiumConflictException("duplicate_rfc", $"Ya existe un cliente con RFC {rfc} en Comercial Premium: {duplicateRfc.Code}.");

        var result = Sdk.fInsertaCteProv();
        EnsureSdk(result, "insert_customer", "No se pudo iniciar el alta del cliente");
        var insertionStarted = true;
        try
        {
            Set("cCodigoCliente", code);
            Set("cRazonSocial", businessName);
            Set("cFechaAlta", DateTime.Today.ToString("MM/dd/yyyy"));
            Set("cRFC", rfc);
            Set("cDenComercial", tradeName);
            Set("cTipoCliente", "1");
            Set("cEstatus", "1");
            Set("cListaPrecioCliente", string.IsNullOrWhiteSpace(_options.PriceList) ? "1" : _options.PriceList.Trim());
            SetOptional("cEmail1", request.Email);
            SetOptional("cCon1Tel", request.Phone);
            SetOptional("cCon1Nom", request.ContactName);

            result = Sdk.fGuardaCteProv();
            EnsureSdk(result, "save_customer", "Comercial Premium rechazó el alta del cliente");
            insertionStarted = false;
        }
        finally
        {
            if (insertionStarted)
            {
                try { Sdk.fCancelarModificacionCteProv(); } catch { }
            }
        }

        var created = FindByCode(code);
        if (created is null)
            throw new PremiumSdkException("verify_customer", "El SDK guardó el cliente pero no pudo volver a localizarlo.", null);

        _logger.LogInformation("Cliente {Code} RFC {Rfc} creado mediante SDK en {Company}", code, rfc, _options.CompanyDirectory);
        return created;
    }, ct);

    public Task<QuoteResult> CreateQuoteAsync(CreateQuoteRequest request, CancellationToken ct) => ExecuteAsync(() =>
    {
        EnsureWritesAllowed();
        var customerCode = NormalizeCode(request.CustomerCode);
        if (string.IsNullOrWhiteSpace(customerCode)) throw new PremiumValidationException("validation_customer", "El código de cliente es obligatorio.");
        if (FindByCode(customerCode) is null) throw new PremiumValidationException("customer_not_found", $"No existe el cliente {customerCode} en Comercial Premium.");
        if (request.Items is null || request.Items.Count == 0) throw new PremiumValidationException("validation_items", "La cotización requiere al menos una partida.");
        if (request.Items.Any(x => string.IsNullOrWhiteSpace(x.ProductCode))) throw new PremiumValidationException("validation_product", "Todas las partidas requieren código de producto.");
        if (request.Items.Any(x => x.Quantity <= 0 || x.UnitPrice < 0)) throw new PremiumValidationException("validation_amounts", "Cantidad y precio de las partidas no son válidos.");

        var concept = string.IsNullOrWhiteSpace(_options.QuoteConceptCode) ? "1" : _options.QuoteConceptCode.Trim();
        var requestedSeries = (string.IsNullOrWhiteSpace(_options.QuoteSeries) ? "CZM" : _options.QuoteSeries.Trim()).ToUpperInvariant();
        // fSiguienteFolio recibe la serie como buffer de entrada/salida. Comercial puede modificar
        // ese buffer; no usamos ese valor mutado como serie oficial. La serie oficial es la
        // configurada por MIDA (igual que en Licencias MIDA) y solo pedimos a Premium el folio.
        var sdkSeries = new StringBuilder(requestedSeries, 12);
        double folio = 0;
        var result = Sdk.fSiguienteFolio(concept, sdkSeries, ref folio);
        EnsureSdk(result, "next_quote_folio", "No se pudo obtener el siguiente folio de cotización");

        var document = SdkDocument.Create(
            concept,
            requestedSeries,
            folio,
            customerCode,
            DateTime.Today.ToString("MM/dd/yyyy"));
        var documentId = 0;
        result = Sdk.fAltaDocumento(ref documentId, ref document);
        EnsureSdk(result, "create_quote_document", "No se pudo crear el documento de cotización");

        var movementLinks = new List<QuoteMovementResult>();
        try
        {
            var movementIndex = 0;
            foreach (var item in request.Items)
            {
                movementIndex++;
                var movement = SdkMovement.Create(
                    NormalizeCode(item.ProductCode),
                    string.IsNullOrWhiteSpace(_options.DefaultWarehouse) ? "1" : _options.DefaultWarehouse.Trim(),
                    (double)item.Quantity,
                    (double)item.UnitPrice,
                    movementIndex);
                var movementId = 0;
                result = Sdk.fAltaMovimiento(documentId, ref movementId, ref movement);
                EnsureSdk(result, $"create_quote_movement_{movementIndex}", $"No se pudo crear la partida {movementIndex} ({item.ProductCode})");
                movementLinks.Add(new QuoteMovementResult(movementIndex - 1, movementId));
            }

            if (!string.IsNullOrWhiteSpace(request.Observations))
            {
                result = Sdk.fBuscarIdDocumento(documentId);
                EnsureSdk(result, "find_created_quote", "No se pudo volver a posicionar la cotización creada");
                result = Sdk.fEditarDocumento();
                EnsureSdk(result, "edit_created_quote", "No se pudo editar la cotización para guardar observaciones");
                result = Sdk.fSetDatoDocumento("COBSERVACIONES", request.Observations.Trim());
                EnsureSdk(result, "set_quote_observations", "No se pudieron guardar las observaciones de la cotización");
                result = Sdk.fGuardaDocumento();
                EnsureSdk(result, "save_quote_observations", "No se pudieron confirmar las observaciones de la cotización");
            }
        }
        catch
        {
            _logger.LogError("La cotización {Series}-{Folio} se alcanzó a crear en Premium, pero falló una partida o dato posterior. DocumentId={DocumentId}", requestedSeries, folio, documentId);
            throw;
        }

        Guid? documentGuid = null;
        try
        {
            if (Sdk.fBuscarIdDocumento(documentId) == 0)
            {
                var value = new StringBuilder(64);
                if (Sdk.fLeeDatoDocumento("CGUIDDOCUMENTO", value, value.Capacity) == 0 && Guid.TryParse(value.ToString().Trim(), out var parsed))
                    documentGuid = parsed;
            }
        }
        catch { }

        var total = request.Items.Sum(x => x.Quantity * x.UnitPrice * (1m + Math.Max(0m, x.TaxRate) / 100m));
        _logger.LogInformation("Cotización {Series}-{Folio} creada mediante SDK. DocumentId={DocumentId}, Cliente={Customer}. Serie SDK devuelta={SdkSeries}", requestedSeries, folio, documentId, customerCode, sdkSeries.ToString().Trim());
        return new QuoteResult(documentId, documentGuid, requestedSeries, (decimal)folio, decimal.Round(total, 2), movementLinks);
    }, ct);

    public Task<UpdateQuoteResult> UpdateQuoteAsync(int documentId, UpdateQuoteRequest request, CancellationToken ct) => ExecuteAsync(() =>
    {
        var totalWatch = Stopwatch.StartNew();
        var stageWatch = Stopwatch.StartNew();
        EnsureWritesAllowed();
        if (documentId <= 0) throw new PremiumValidationException("validation_document", "El identificador del documento Premium no es válido.");
        if (request.Items is null || request.Items.Count == 0) throw new PremiumValidationException("validation_items", "La cotización requiere al menos una partida.");
        if (request.Items.Any(x => string.IsNullOrWhiteSpace(x.ProductCode))) throw new PremiumValidationException("validation_product", "Todas las partidas requieren código de producto.");
        if (request.Items.Any(x => x.Quantity <= 0 || x.UnitPrice < 0)) throw new PremiumValidationException("validation_amounts", "Cantidad y precio no válidos.");

        var result = Sdk.fBuscarIdDocumento(documentId);
        EnsureSdk(result, "find_quote_for_update", $"No se encontró el documento Premium {documentId}");

        var items = request.Items.ToArray();
        var persistedIds = items.Where(x => x.PremiumMovementId.HasValue).Select(x => x.PremiumMovementId!.Value).ToArray();
        if (persistedIds.Any(x => x <= 0))
            throw new PremiumValidationException("premium_movement_invalid", "Una partida contiene un identificador de movimiento Premium inválido.");
        if (persistedIds.Length != persistedIds.Distinct().Count())
            throw new PremiumValidationException("premium_movement_duplicate", "La edición contiene identificadores de movimiento Premium duplicados.");

        var existingCount = persistedIds.Length;
        var newCount = items.Length - existingCount;
        _logger.LogInformation("Edición por vínculos persistidos. Documento={DocumentId}, MovimientosExistentes={ExistingCount}, PartidasNuevas={NewCount}", documentId, existingCount, newCount);

        stageWatch.Restart();
        result = Sdk.fBuscarIdDocumento(documentId);
        EnsureSdk(result, "refind_quote_for_update", "No se pudo volver a posicionar la cotización.");
        result = Sdk.fEditarDocumento();
        EnsureSdk(result, "edit_quote_document", "No se pudo activar la edición de la cotización.");
        try
        {
            var expiration = DateTime.Today.AddDays(Math.Max(1, request.ValidityDays)).ToString("MM/dd/yyyy");
            result = Sdk.fSetDatoDocumento("CFECHAVENCIMIENTO", expiration);
            EnsureSdk(result, "set_quote_expiration", "No se pudo actualizar la vigencia.");
            result = Sdk.fSetDatoDocumento("COBSERVACIONES", (request.Observations ?? string.Empty).Trim());
            EnsureSdk(result, "set_quote_observations_update", "No se pudieron actualizar las observaciones.");
            result = Sdk.fGuardaDocumento();
            EnsureSdk(result, "save_quote_document_update", "No se pudo guardar el encabezado actualizado.");
        }
        catch
        {
            try { Sdk.fCancelaCambiosDocumento(); } catch { }
            throw;
        }

        _logger.LogInformation("Encabezado Premium actualizado en {ElapsedMs} ms. Documento={DocumentId}", stageWatch.ElapsedMilliseconds, documentId);

        var movementLinks = new List<QuoteMovementResult>();
        stageWatch.Restart();

        for (var i = 0; i < items.Length; i++)
        {
            var movementId = items[i].PremiumMovementId ?? 0;

            if (movementId > 0)
            {
                _logger.LogInformation("Editando movimiento Premium exacto {MovementId} para partida {Index}. Documento={DocumentId}", movementId, i + 1, documentId);
                _logger.LogInformation("SDK -> fBuscarIdMovimiento({MovementId})", movementId);
                result = Sdk.fBuscarIdMovimiento(movementId);
                _logger.LogInformation("SDK <- fBuscarIdMovimiento({MovementId}) = {Result}", movementId, result);
                EnsureSdk(result, $"find_quote_movement_{i + 1}", $"No se encontró el movimiento Premium {movementId}.");
                _logger.LogInformation("SDK -> fEditarMovimiento(). MovementId={MovementId}", movementId);
                result = Sdk.fEditarMovimiento();
                _logger.LogInformation("SDK <- fEditarMovimiento(). MovementId={MovementId}, Result={Result}", movementId, result);
                EnsureSdk(result, $"edit_quote_movement_{i + 1}", $"No se pudo editar la partida {i + 1}.");
                try
                {
                    var quantityText = items[i].Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var priceText = items[i].UnitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture);

                    _logger.LogInformation("SDK -> actualizar cantidad y cantidad capturada. MovementId={MovementId}, Valor={Value}", movementId, quantityText);
                    result = Sdk.fSetDatoMovimiento("CUNIDADES", quantityText);
                    EnsureSdk(result, $"set_quote_units_{i + 1}", $"No se pudo actualizar la cantidad de la partida {i + 1}.");
                    result = Sdk.fSetDatoMovimiento("CUNIDADESCAPTURADAS", quantityText);
                    EnsureSdk(result, $"set_quote_captured_units_{i + 1}", $"No se pudo actualizar la cantidad capturada de la partida {i + 1}.");

                    _logger.LogInformation("SDK -> actualizar precio y precio capturado. MovementId={MovementId}, Valor={Value}", movementId, priceText);
                    result = Sdk.fSetDatoMovimiento("CPRECIO", priceText);
                    EnsureSdk(result, $"set_quote_price_{i + 1}", $"No se pudo actualizar el precio de la partida {i + 1}.");
                    result = Sdk.fSetDatoMovimiento("CPRECIOCAPTURADO", priceText);
                    EnsureSdk(result, $"set_quote_captured_price_{i + 1}", $"No se pudo actualizar el precio capturado de la partida {i + 1}.");

                    _logger.LogInformation("SDK -> fGuardaMovimiento(). MovementId={MovementId}", movementId);
                    result = Sdk.fGuardaMovimiento();
                    _logger.LogInformation("SDK <- fGuardaMovimiento(). MovementId={MovementId}, Result={Result}", movementId, result);
                    EnsureSdk(result, $"save_quote_movement_{i + 1}", $"No se pudo guardar la partida {i + 1}.");

                    result = Sdk.fBuscarIdMovimiento(movementId);
                    EnsureSdk(result, $"verify_quote_movement_{i + 1}", $"No se pudo volver a localizar la partida {i + 1} para verificarla.");
                    var units = ReadMovementValue("CUNIDADES");
                    var capturedUnits = ReadMovementValue("CUNIDADESCAPTURADAS");
                    var price = ReadMovementValue("CPRECIO");
                    var capturedPrice = ReadMovementValue("CPRECIOCAPTURADO");
                    _logger.LogInformation(
                        "Verificación Premium MovementId={MovementId}: CUNIDADES={Units}, CUNIDADESCAPTURADAS={CapturedUnits}, CPRECIO={Price}, CPRECIOCAPTURADO={CapturedPrice}",
                        movementId, units, capturedUnits, price, capturedPrice);
                }
                catch
                {
                    try { Sdk.fCancelaCambiosMovimiento(); } catch { }
                    throw;
                }
                movementLinks.Add(new QuoteMovementResult(i, movementId));
                continue;
            }

            _logger.LogInformation("Creando movimiento Premium nuevo para partida {Index}. Documento={DocumentId}, Producto={ProductCode}", i + 1, documentId, items[i].ProductCode);
            var movement = SdkMovement.Create(
                NormalizeCode(items[i].ProductCode),
                string.IsNullOrWhiteSpace(_options.DefaultWarehouse) ? "1" : _options.DefaultWarehouse.Trim(),
                (double)items[i].Quantity,
                (double)items[i].UnitPrice,
                i + 1);
            var newMovementId = 0;
            _logger.LogInformation("SDK -> fAltaMovimiento(). Documento={DocumentId}, Partida={Index}, Producto={ProductCode}", documentId, i + 1, items[i].ProductCode);
            result = Sdk.fAltaMovimiento(documentId, ref newMovementId, ref movement);
            _logger.LogInformation("SDK <- fAltaMovimiento(). Documento={DocumentId}, Partida={Index}, MovementId={MovementId}, Result={Result}", documentId, i + 1, newMovementId, result);
            EnsureSdk(result, $"append_quote_movement_{i + 1}", $"No se pudo agregar la partida {i + 1} ({items[i].ProductCode}) a la cotización Premium.");
            movementLinks.Add(new QuoteMovementResult(i, newMovementId));
        }

        _logger.LogInformation("Sincronización de movimientos por ID completada en {ElapsedMs} ms. Documento={DocumentId}, Partidas={Count}", stageWatch.ElapsedMilliseconds, documentId, items.Length);

        totalWatch.Stop();
        _logger.LogInformation("Cotización Premium {DocumentId} actualizada mediante SDK. Tiempo total={ElapsedMs} ms. Enviando ACK al CRM.", documentId, totalWatch.ElapsedMilliseconds);
        return new UpdateQuoteResult(true, documentId, movementLinks);
    }, ct);

    private void SdkThreadMain()
    {
        _sdkThreadId = Environment.CurrentManagedThreadId;
        _logger.LogInformation("Hilo persistente del SDK iniciado. ManagedThreadId={ThreadId}", _sdkThreadId);

        var initialized = false;
        var opened = false;
        var originalDirectory = Environment.CurrentDirectory;
        try
        {
            ValidateInstallation();
            if (Environment.Is64BitProcess)
                throw new PremiumSdkException("wrong_architecture", "Premium Agent debe ejecutarse como proceso x86/32 bits.", null);

            if (!NativeMethods.SetDllDirectory(_options.SdkDirectory))
                throw new PremiumSdkException("dll_directory", $"No fue posible configurar {_options.SdkDirectory} como directorio del SDK.", Marshal.GetLastWin32Error().ToString());

            Environment.CurrentDirectory = _options.SdkDirectory;
            _logger.LogInformation("Inicializando sesión persistente del SDK en hilo {ThreadId}", _sdkThreadId);

            var result = Sdk.fInicializaSDK();
            EnsureSdk(result, "sdk_initialize", "No se pudo inicializar el SDK de Comercial Premium");
            initialized = true;

            result = Sdk.fAbreEmpresa(_options.CompanyDirectory);
            EnsureSdk(result, "company_open", "No se pudo abrir la empresa configurada de Comercial Premium");
            opened = true;

            var dll = Path.Combine(_options.SdkDirectory, "MGWServicios.dll");
            _sdkVersion = FileVersionInfo.GetVersionInfo(dll).FileVersion ?? "desconocida";
            _sessionReady = true;
            _lastError = null;
            _logger.LogInformation("Sesión persistente SDK lista. Empresa {Company} abierta; no se volverá a abrir por cada petición.", _options.CompanyDirectory);
            _started.TrySetResult(true);

            foreach (var item in _queue.GetConsumingEnumerable())
            {
                try
                {
                    var value = item.Action();
                    item.Completion.TrySetResult(value);
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetException(ex);
                }
            }
        }
        catch (Exception ex)
        {
            _sessionReady = false;
            _lastError = ex.Message;
            _logger.LogError(ex, "No fue posible iniciar o mantener la sesión persistente del SDK");
            _started.TrySetException(ex);

            while (_queue.TryTake(out var pending))
                pending.Completion.TrySetException(ex);
        }
        finally
        {
            _sessionReady = false;
            if (opened)
            {
                try
                {
                    Sdk.fCierraEmpresa();
                    _logger.LogInformation("Empresa de Comercial Premium cerrada.");
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Error al cerrar la empresa del SDK"); }
            }

            if (initialized)
            {
                try
                {
                    Sdk.fTerminaSDK();
                    _logger.LogInformation("SDK de Comercial Premium finalizado.");
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Error al finalizar el SDK"); }
            }

            NativeMethods.SetDllDirectory(null);
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<T> action, CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PremiumSdkGateway));
        await StartAsync(ct);
        ct.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new SdkWorkItem(() => action(), completion);
        try
        {
            _queue.Add(item, ct);
        }
        catch (InvalidOperationException)
        {
            throw new PremiumSdkException("sdk_session_closed", "La sesión persistente del SDK ya está cerrada.", null);
        }

        using var registration = ct.Register(() => completion.TrySetCanceled(ct));
        var result = await completion.Task;
        return (T)result!;
    }

    private void ValidateInstallation()
    {
        var dll = Path.Combine(_options.SdkDirectory, "MGWServicios.dll");
        var cac = Path.Combine(_options.SdkDirectory, "CAC.ini");
        if (!File.Exists(dll)) throw new PremiumSdkException("sdk_dll_missing", $"No se encontró {dll}.", null);
        if (!File.Exists(cac)) throw new PremiumSdkException("cac_missing", $"No se encontró {cac}.", null);
        if (!Directory.Exists(_options.CompanyDirectory)) throw new PremiumSdkException("company_missing", $"No existe {_options.CompanyDirectory}.", null);
    }

    private void EnsureWritesAllowed()
    {
        if (!_options.AllowWrites)
            throw new PremiumValidationException("writes_disabled", "Las escrituras del Premium Agent están deshabilitadas.");
        var actual = Path.GetFileName(_options.CompanyDirectory.TrimEnd('\\', '/'));
        if (!actual.Equals(_options.AllowedCompanyDirectoryName, StringComparison.OrdinalIgnoreCase))
            throw new PremiumValidationException("company_not_allowed", $"Escritura bloqueada: {actual} no coincide con la empresa permitida {_options.AllowedCompanyDirectoryName}.");
    }

    private CustomerResult? FindByCode(string input)
    {
        var code = NormalizeCode(input);
        if (string.IsNullOrWhiteSpace(code)) return null;
        if (Sdk.fBuscaCteProv(code) != 0) return null;
        return ReadCurrentCustomer();
    }

    private CustomerResult? FindByRfc(string input)
    {
        var target = NormalizeRfc(input);
        if (string.IsNullOrWhiteSpace(target)) return null;
        var result = Sdk.fPosPrimerCteProv();
        while (result == 0)
        {
            var rfc = NormalizeRfc(Read("cRFC"));
            if (rfc == target) return ReadCurrentCustomer();
            result = Sdk.fPosSiguienteCteProv();
        }
        return null;
    }

    private static CustomerResult ReadCurrentCustomer() => new(
        Read("cCodigoCliente").Trim(),
        Read("cRazonSocial").Trim(),
        NullIfEmpty(Read("cDenComercial")),
        NormalizeRfc(Read("cRFC")),
        NullIfEmpty(Read("cEmail1")),
        NullIfEmpty(Read("cCon1Tel")),
        NullIfEmpty(Read("cCon1Nom")),
        NullIfEmpty(Read("cListaPrecioCliente")));

    private static void SetOptional(string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) Set(field, value.Trim());
    }

    private static void Set(string field, string value)
    {
        var result = Sdk.fSetDatoCteProv(field, value);
        EnsureSdk(result, $"set_{field}", $"No se pudo asignar {field}");
    }

    private static string Read(string field)
    {
        var value = new StringBuilder(512);
        var result = Sdk.fLeeDatoCteProv(field, value, value.Capacity);
        EnsureSdk(result, $"read_{field}", $"No se pudo leer {field}");
        return value.ToString();
    }

    private static string ReadMovementValue(string field)
    {
        var value = new StringBuilder(128);
        var result = Sdk.fLeeDatoMovimiento(field, value, value.Capacity);
        EnsureSdk(result, $"read_movement_{field}", $"No se pudo leer {field} del movimiento.");
        return value.ToString().Trim();
    }

    private static void EnsureSdk(int result, string code, string message)
    {
        if (result == 0) return;
        throw new PremiumSdkException(code, message, GetSdkError(result));
    }

    private static string GetSdkError(int error)
    {
        try
        {
            var message = new StringBuilder(512);
            Sdk.fError(error, message, message.Capacity);
            var detail = message.ToString().Trim();
            return string.IsNullOrWhiteSpace(detail) ? $"SDK error {error}" : $"{error}: {detail}";
        }
        catch { return $"SDK error {error}"; }
    }

    private static string NormalizeRfc(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant().Replace(" ", string.Empty);
    private static string NormalizeCode(string? value) => new string((value ?? string.Empty).Trim().ToUpperInvariant().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
    private static string BuildCode(string rfc)
    {
        var clean = new string(rfc.Where(char.IsLetterOrDigit).ToArray());
        var suffix = clean.Length > 20 ? clean[..20] : clean;
        return $"CRM{suffix}";
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _queue.CompleteAdding(); } catch { }
        if (Thread.CurrentThread != _sdkThread && _sdkThread.IsAlive)
            _sdkThread.Join(TimeSpan.FromSeconds(15));
        _queue.Dispose();
    }

    private sealed record SdkWorkItem(Func<object?> Action, TaskCompletionSource<object?> Completion);
}

public sealed class PremiumConflictException(string code, string message) : Exception(message) { public string Code { get; } = code; }
public sealed class PremiumValidationException(string code, string message) : Exception(message) { public string Code { get; } = code; }
public sealed class PremiumSdkException(string code, string message, string? sdkError) : Exception(sdkError is null ? message : $"{message}. {sdkError}")
{
    public string Code { get; } = code;
    public string? SdkError { get; } = sdkError;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 4)]
internal struct SdkDocument
{
    public double aFolio;
    public int aNumMoneda;
    public double aTipoCambio;
    public double aImporte;
    public double aDescuentoDoc1;
    public double aDescuentoDoc2;
    public int aSistemaOrigen;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 31)] public string aCodConcepto;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 12)] public string aSerie;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 24)] public string aFecha;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 31)] public string aCodigoCteProv;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 31)] public string aCodigoAgente;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string aReferencia;
    public int aAfecta;
    public double aGasto1;
    public double aGasto2;
    public double aGasto3;

    public static SdkDocument Create(string concept, string series, double folio, string customer, string date) => new()
    {
        aFolio = folio, aNumMoneda = 1, aTipoCambio = 1, aImporte = 0, aDescuentoDoc1 = 0, aDescuentoDoc2 = 0,
        aSistemaOrigen = 205, aCodConcepto = concept, aSerie = series, aFecha = date, aCodigoCteProv = customer,
        aCodigoAgente = string.Empty, aReferencia = "CRM MIDA", aAfecta = 0, aGasto1 = 0, aGasto2 = 0, aGasto3 = 0
    };
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 4)]
internal struct SdkMovement
{
    public int aConsecutivo;
    public double aUnidades;
    public double aPrecio;
    public double aCosto;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 31)] public string aCodProdSer;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 31)] public string aCodAlmacen;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string aReferencia;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 31)] public string aCodClasificacion;

    public static SdkMovement Create(string product, string warehouse, double units, double price, int consecutive) => new()
    {
        aConsecutivo = consecutive, aUnidades = units, aPrecio = price, aCosto = 0, aCodProdSer = product,
        aCodAlmacen = warehouse, aReferencia = "CRM MIDA", aCodClasificacion = string.Empty
    };
}

internal static class Sdk
{
    private const string DllName = "MGWServicios.dll";
    [DllImport(DllName, EntryPoint = "fInicializaSDK", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)] internal static extern int fInicializaSDK();
    [DllImport(DllName, EntryPoint = "fAbreEmpresa", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)] internal static extern int fAbreEmpresa([MarshalAs(UnmanagedType.LPStr)] string directorio);
    [DllImport(DllName, EntryPoint = "fCierraEmpresa", CallingConvention = CallingConvention.Cdecl)] internal static extern void fCierraEmpresa();
    [DllImport(DllName, EntryPoint = "fTerminaSDK", CallingConvention = CallingConvention.Cdecl)] internal static extern void fTerminaSDK();
    [DllImport(DllName, EntryPoint = "fError", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)] internal static extern void fError(int error, StringBuilder mensaje, int len);
    [DllImport(DllName, EntryPoint = "fBuscaCteProv", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)] internal static extern int fBuscaCteProv([MarshalAs(UnmanagedType.LPStr)] string codigo);
    [DllImport(DllName, EntryPoint = "fPosPrimerCteProv", CallingConvention = CallingConvention.Cdecl)] internal static extern int fPosPrimerCteProv();
    [DllImport(DllName, EntryPoint = "fPosSiguienteCteProv", CallingConvention = CallingConvention.Cdecl)] internal static extern int fPosSiguienteCteProv();
    [DllImport(DllName, EntryPoint = "fInsertaCteProv", CallingConvention = CallingConvention.Cdecl)] internal static extern int fInsertaCteProv();
    [DllImport(DllName, EntryPoint = "fSetDatoCteProv", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)] internal static extern int fSetDatoCteProv([MarshalAs(UnmanagedType.LPStr)] string campo, [MarshalAs(UnmanagedType.LPStr)] string valor);
    [DllImport(DllName, EntryPoint = "fLeeDatoCteProv", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)] internal static extern int fLeeDatoCteProv([MarshalAs(UnmanagedType.LPStr)] string campo, StringBuilder valor, int len);
    [DllImport(DllName, EntryPoint = "fGuardaCteProv", CallingConvention = CallingConvention.Cdecl)] internal static extern int fGuardaCteProv();
    [DllImport(DllName, EntryPoint = "fCancelarModificacionCteProv", CallingConvention = CallingConvention.Cdecl)] internal static extern int fCancelarModificacionCteProv();
    [DllImport(DllName, EntryPoint = "fSiguienteFolio", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)] internal static extern int fSiguienteFolio([MarshalAs(UnmanagedType.LPStr)] string codigoConcepto, StringBuilder serie, ref double folio);
    [DllImport(DllName, EntryPoint = "fAltaDocumento", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)] internal static extern int fAltaDocumento(ref int idDocumento, ref SdkDocument documento);
    [DllImport(DllName, EntryPoint = "fAltaMovimiento", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)] internal static extern int fAltaMovimiento(int idDocumento, ref int idMovimiento, ref SdkMovement movimiento);
    [DllImport(DllName, EntryPoint = "fBuscarIdDocumento", CallingConvention = CallingConvention.Cdecl)] internal static extern int fBuscarIdDocumento(int idDocumento);
    [DllImport(DllName, EntryPoint = "fEditarDocumento", CallingConvention = CallingConvention.Cdecl)] internal static extern int fEditarDocumento();
    [DllImport(DllName, EntryPoint = "fSetDatoDocumento", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)] internal static extern int fSetDatoDocumento([MarshalAs(UnmanagedType.LPStr)] string campo, [MarshalAs(UnmanagedType.LPStr)] string valor);
    [DllImport(DllName, EntryPoint = "fGuardaDocumento", CallingConvention = CallingConvention.Cdecl)] internal static extern int fGuardaDocumento();
    [DllImport(DllName, EntryPoint = "fCancelaCambiosDocumento", CallingConvention = CallingConvention.Cdecl)] internal static extern int fCancelaCambiosDocumento();
    [DllImport(DllName, EntryPoint = "fLeeDatoDocumento", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)] internal static extern int fLeeDatoDocumento([MarshalAs(UnmanagedType.LPStr)] string campo, StringBuilder valor, int len);
    [DllImport(DllName, EntryPoint = "fSetFiltroMovimiento", CallingConvention = CallingConvention.Cdecl)] internal static extern int fSetFiltroMovimiento(int idDocumento);
    [DllImport(DllName, EntryPoint = "fCancelaFiltroMovimiento", CallingConvention = CallingConvention.Cdecl)] internal static extern int fCancelaFiltroMovimiento();
    [DllImport(DllName, EntryPoint = "fPosPrimerMovimiento", CallingConvention = CallingConvention.Cdecl)] internal static extern int fPosPrimerMovimiento();
    [DllImport(DllName, EntryPoint = "fPosSiguienteMovimiento", CallingConvention = CallingConvention.Cdecl)] internal static extern int fPosSiguienteMovimiento();
    [DllImport(DllName, EntryPoint = "fBuscarIdMovimiento", CallingConvention = CallingConvention.Cdecl)] internal static extern int fBuscarIdMovimiento(int idMovimiento);
    [DllImport(DllName, EntryPoint = "fEditarMovimiento", CallingConvention = CallingConvention.Cdecl)] internal static extern int fEditarMovimiento();
    [DllImport(DllName, EntryPoint = "fSetDatoMovimiento", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)] internal static extern int fSetDatoMovimiento([MarshalAs(UnmanagedType.LPStr)] string campo, [MarshalAs(UnmanagedType.LPStr)] string valor);
    [DllImport(DllName, EntryPoint = "fLeeDatoMovimiento", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)] internal static extern int fLeeDatoMovimiento([MarshalAs(UnmanagedType.LPStr)] string campo, StringBuilder valor, int len);
    [DllImport(DllName, EntryPoint = "fGuardaMovimiento", CallingConvention = CallingConvention.Cdecl)] internal static extern int fGuardaMovimiento();
    [DllImport(DllName, EntryPoint = "fCancelaCambiosMovimiento", CallingConvention = CallingConvention.Cdecl)] internal static extern int fCancelaCambiosMovimiento();
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetDllDirectory(string? path);
}
