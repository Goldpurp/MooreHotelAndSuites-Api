using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.DTOs.Pricing;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Common;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;
using DailyRateDto = MooreHotels.Application.DTOs.Pricing.DailyRoomRate;
using PromotionDto = MooreHotels.Application.DTOs.Pricing.Promotion;
using RatePlanDto = MooreHotels.Application.DTOs.Pricing.RatePlan;
using RuleDto = MooreHotels.Application.DTOs.Pricing.PricingRule;
using DailyRateEntity = MooreHotels.Domain.Entities.DailyRoomRate;
using PromotionEntity = MooreHotels.Domain.Entities.Promotion;
using RatePlanEntity = MooreHotels.Domain.Entities.RatePlan;
using RuleEntity = MooreHotels.Domain.Entities.PricingRule;

namespace MooreHotels.Infrastructure.Services;

public sealed class PricingService : IPricingService
{
    private readonly MooreHotelsDbContext _db;
    private readonly IHotelTimeService _hotelTime;
    private readonly IInventoryService _inventory;
    private readonly PricingSettings _settings;

    public PricingService(
        MooreHotelsDbContext db,
        IHotelTimeService hotelTime,
        IInventoryService inventory,
        IOptions<PricingSettings> settings)
    {
        _db = db;
        _hotelTime = hotelTime;
        _inventory = inventory;
        _settings = settings.Value;
    }

    public Task<PricingQuoteDto> CreateQuoteAsync(
        CreatePricingQuoteRequest request,
        CancellationToken cancellationToken = default) =>
        CreateQuoteCoreAsync(request, null, null, cancellationToken);

    public async Task<PricingQuoteDto> CreateAmendmentQuoteAsync(
        Guid bookingId,
        CreatePricingQuoteRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        await RequireReservationActorAsync(actorId, cancellationToken);
        if (bookingId == Guid.Empty) throw new NotFoundException("Booking not found.");
        var booking = await _db.Bookings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == bookingId, cancellationToken)
            ?? throw new NotFoundException("Booking not found.");
        if (booking.Status is not (BookingStatus.Pending or BookingStatus.Confirmed))
            throw new BadRequestException("Only pending or confirmed reservations can be repriced.");
        if (DateTime.UtcNow >= booking.CheckIn)
            throw new BadRequestException("A reservation cannot be repriced after its check-in time.");

        return await CreateQuoteCoreAsync(request, bookingId, actorId, cancellationToken);
    }

    private async Task<PricingQuoteDto> CreateQuoteCoreAsync(
        CreatePricingQuoteRequest request,
        Guid? excludedBookingId,
        Guid? actorId,
        CancellationToken cancellationToken)
    {
        var checkInDate = DateOnly.FromDateTime(request.CheckIn);
        var checkOutDate = DateOnly.FromDateTime(request.CheckOut);
        ValidateStay(checkInDate, checkOutDate, request.AdultCount, request.ChildCount);
        if (request.RoomId == Guid.Empty || request.RoomTypeId == Guid.Empty)
            throw new BadRequestException("Room or room-type identifier is invalid.");

        if (request.RoomId.HasValue == request.RoomTypeId.HasValue)
            throw new BadRequestException("Select exactly one inventory scope: a room type or a legacy physical room.");
        if (request.RoomQuantity is < 1 or > 10)
            throw new BadRequestException("Room quantity must be between 1 and 10.");

        Room? room = null;
        RoomType roomType;
        if (request.RoomId.HasValue)
        {
            if (request.RoomQuantity != 1)
                throw new BadRequestException("A legacy physical-room quote can contain only one room.");
            room = await _db.Rooms.AsNoTracking().Include(item => item.RoomType)
                .SingleOrDefaultAsync(item => item.Id == request.RoomId.Value, cancellationToken)
                ?? throw new NotFoundException("Room not found.");
            if (!room.IsOnline || room.Status is RoomStatus.Maintenance or RoomStatus.OutOfOrder)
                throw new BadRequestException("This room is currently unavailable.");
            roomType = room.RoomType
                ?? throw new InvalidOperationException("The room has no configured room type.");
            if (!roomType.IsActive)
                throw new BadRequestException("This room type is inactive.");

            var startUtc = _hotelTime.GetCheckInUtc(request.CheckIn);
            var endUtc = _hotelTime.GetCheckOutUtc(request.CheckOut);
            var expirationCutoff = BookingPaymentPolicy.GetExpirationCutoffUtc(DateTime.UtcNow);
            if (await _db.Bookings.AsNoTracking().AnyAsync(existing =>
                    (!excludedBookingId.HasValue || existing.Id != excludedBookingId.Value) &&
                    (existing.ReservationRooms.Any(item => item.AssignedRoomId == room.Id) ||
                     !existing.ReservationRooms.Any() && existing.RoomId == room.Id) &&
                    existing.Status != BookingStatus.Cancelled &&
                    existing.Status != BookingStatus.CheckedOut &&
                    existing.Status != BookingStatus.NoShow &&
                    !(existing.Status == BookingStatus.Pending &&
                      (existing.PaymentStatus == PaymentStatus.Unpaid ||
                       existing.PaymentStatus == PaymentStatus.AwaitingVerification) &&
                      existing.CreatedAt <= expirationCutoff) &&
                    existing.CheckIn < endUtc && existing.CheckOut > startUtc,
                    cancellationToken))
            {
                throw new BadRequestException("This room is already assigned during the requested stay.");
            }
        }
        else
        {
            roomType = await _db.RoomTypes.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.Id == request.RoomTypeId!.Value && item.IsActive,
                    cancellationToken)
                ?? throw new NotFoundException("Room type not found or inactive.");
            var availability = excludedBookingId.HasValue
                ? await _inventory.GetAvailabilityExcludingBookingAsync(
                    roomType.Id, checkInDate, checkOutDate, request.RoomQuantity,
                    excludedBookingId.Value, cancellationToken)
                : await _inventory.GetAvailabilityAsync(
                    roomType.Id, checkInDate, checkOutDate, request.RoomQuantity, cancellationToken);
            if (!availability.Available)
                throw new BadRequestException(
                    $"Only {availability.AvailableUnits} {roomType.Name} room(s) remain for the full stay.");
        }
        var maximumOccupancy = checked(roomType.MaxOccupancy * request.RoomQuantity);
        if (checked(request.AdultCount + request.ChildCount) > maximumOccupancy)
            throw new BadRequestException(
                $"The selected inventory permits a maximum of {maximumOccupancy} guests.");

        var requestedRatePlan = NormalizeOptionalCode(request.RatePlanCode, "rate plan", 30);
        var ratePlanQuery = _db.RatePlans.AsNoTracking().Where(plan => plan.IsActive);
        var ratePlan = requestedRatePlan is null
            ? await ratePlanQuery.SingleOrDefaultAsync(plan => plan.IsDefault, cancellationToken)
            : await ratePlanQuery.SingleOrDefaultAsync(
                plan => plan.Code == requestedRatePlan,
                cancellationToken);
        if (ratePlan is null)
            throw new BadRequestException("The selected rate plan is not available.");

        var nights = checkOutDate.DayNumber - checkInDate.DayNumber;
        if (nights < ratePlan.MinimumNights || nights > ratePlan.MaximumNights)
        {
            throw new BadRequestException(
                $"Rate plan {ratePlan.Code} permits stays from {ratePlan.MinimumNights} to {ratePlan.MaximumNights} nights.");
        }
        if (ratePlan.SellFromDate.HasValue && checkInDate < ratePlan.SellFromDate ||
            ratePlan.SellUntilDate.HasValue && checkOutDate.AddDays(-1) > ratePlan.SellUntilDate)
        {
            throw new BadRequestException("The selected rate plan is not offered for the full stay.");
        }

        var dailyRates = await _db.DailyRoomRates.AsNoTracking()
            .Where(rate =>
                rate.RatePlanId == ratePlan.Id &&
                rate.StayDate >= checkInDate &&
                rate.StayDate < checkOutDate &&
                (room != null && rate.RoomId == room.Id ||
                 rate.RoomTypeId == roomType.Id ||
                 rate.RoomCategory == roomType.Category))
            .ToListAsync(cancellationToken);

        var quoteId = Guid.NewGuid();
        var quoteLines = new List<BookingQuoteLine>();
        var nightlyAmounts = new Dictionary<DateOnly, decimal>();
        var sortOrder = 0;
        for (var date = checkInDate; date < checkOutDate; date = date.AddDays(1))
        {
            var overrideRate = room is null ? null : dailyRates.FirstOrDefault(rate =>
                                   rate.StayDate == date && rate.RoomId == room.Id);
            overrideRate ??= dailyRates.FirstOrDefault(rate =>
                                   rate.StayDate == date && rate.RoomTypeId == roomType.Id)
                               ?? dailyRates.FirstOrDefault(rate =>
                                   rate.StayDate == date && rate.RoomCategory == roomType.Category);
            var unitAmount = overrideRate is null
                ? ApplyRatePlanAdjustment(room?.PricePerNight ?? roomType.BasePricePerNight, ratePlan)
                : Money(overrideRate.Amount);
            var amount = Money(unitAmount * request.RoomQuantity);
            nightlyAmounts[date] = amount;
            quoteLines.Add(new BookingQuoteLine
            {
                Id = Guid.NewGuid(),
                BookingQuoteId = quoteId,
                Type = PricingLineType.RoomNight,
                Code = overrideRate is null ? ratePlan.Code : $"{ratePlan.Code}-DAILY",
                Description = $"{roomType.Name} — {date:yyyy-MM-dd}",
                StayDate = date,
                Quantity = request.RoomQuantity,
                UnitAmount = unitAmount,
                Amount = amount,
                SortOrder = sortOrder++
            });
        }

        var roomSubtotal = Money(nightlyAmounts.Values.Sum());
        PromotionEntity? promotion = null;
        var discountAmount = 0m;
        var promotionCode = NormalizeOptionalCode(request.PromotionCode, "promotion", 40);
        var now = DateTime.UtcNow;
        if (promotionCode is not null)
        {
            promotion = await _db.Promotions.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Code == promotionCode, cancellationToken);
            if (promotion is null ||
                !promotion.IsActive ||
                promotion.ValidFromUtc > now ||
                promotion.ValidUntilUtc <= now ||
                promotion.MinimumNights > nights ||
                promotion.Currency != ratePlan.Currency ||
                promotion.RatePlanId.HasValue && promotion.RatePlanId != ratePlan.Id ||
                promotion.RedemptionLimit.HasValue &&
                promotion.RedemptionCount >= promotion.RedemptionLimit)
            {
                throw new BadRequestException("The promotion is invalid or no longer available.");
            }

            discountAmount = promotion.DiscountType == DiscountType.Percentage
                ? Money(roomSubtotal * promotion.Value / 100m)
                : Money(promotion.Value);
            if (promotion.MaximumDiscountAmount.HasValue)
                discountAmount = Math.Min(discountAmount, promotion.MaximumDiscountAmount.Value);
            discountAmount = Math.Min(discountAmount, roomSubtotal);
            quoteLines.Add(new BookingQuoteLine
            {
                Id = Guid.NewGuid(),
                BookingQuoteId = quoteId,
                Type = PricingLineType.Discount,
                Code = promotion.Code,
                Description = promotion.Name,
                Quantity = 1,
                UnitAmount = discountAmount,
                Amount = discountAmount,
                SortOrder = sortOrder++
            });
        }

        var rules = await _db.PricingRules.AsNoTracking()
            .Where(rule =>
                rule.IsActive &&
                rule.Currency == ratePlan.Currency &&
                (!rule.EffectiveFromDate.HasValue || rule.EffectiveFromDate < checkOutDate) &&
                (!rule.EffectiveUntilDate.HasValue || rule.EffectiveUntilDate >= checkInDate))
            .OrderBy(rule => rule.SortOrder)
            .ThenBy(rule => rule.Code)
            .ToListAsync(cancellationToken);
        var discountRatio = roomSubtotal == 0m ? 0m : discountAmount / roomSubtotal;
        var includedTaxAmount = 0m;
        var taxAmount = 0m;
        var feeAmount = 0m;
        foreach (var rule in rules)
        {
            var applicableDates = nightlyAmounts.Keys
                .Where(date =>
                    (!rule.EffectiveFromDate.HasValue || date >= rule.EffectiveFromDate) &&
                    (!rule.EffectiveUntilDate.HasValue || date <= rule.EffectiveUntilDate))
                .ToArray();
            if (applicableDates.Length == 0) continue;

            var applicableRoomAmount = Money(applicableDates.Sum(date => nightlyAmounts[date]) *
                                             (1m - discountRatio));
            decimal ruleAmount;
            if (rule.IsInclusive)
            {
                ruleAmount = Money(
                    applicableRoomAmount -
                    applicableRoomAmount / (1m + rule.Value / 100m));
                includedTaxAmount += ruleAmount;
            }
            else
            {
                ruleAmount = rule.Calculation switch
                {
                    PricingRuleCalculation.Percentage =>
                        Money(applicableRoomAmount * rule.Value / 100m),
                    PricingRuleCalculation.FixedPerNight =>
                        Money(rule.Value * applicableDates.Length * request.RoomQuantity),
                    PricingRuleCalculation.FixedPerStay => Money(rule.Value),
                    _ => throw new InvalidOperationException("Unsupported pricing rule calculation.")
                };
                if (rule.Kind == PricingRuleKind.Tax) taxAmount += ruleAmount;
                else feeAmount += ruleAmount;
            }

            quoteLines.Add(new BookingQuoteLine
            {
                Id = Guid.NewGuid(),
                BookingQuoteId = quoteId,
                Type = rule.Kind == PricingRuleKind.Tax
                    ? PricingLineType.Tax
                    : PricingLineType.Fee,
                Code = rule.Code,
                Description = rule.Name,
                Quantity = rule.Calculation == PricingRuleCalculation.FixedPerNight
                    ? applicableDates.Length * request.RoomQuantity
                    : 1,
                UnitAmount = rule.Calculation == PricingRuleCalculation.FixedPerNight
                    ? Money(rule.Value)
                    : ruleAmount,
                Amount = ruleAmount,
                IsInclusive = rule.IsInclusive,
                SortOrder = sortOrder++
            });
        }

        includedTaxAmount = Money(includedTaxAmount);
        taxAmount = Money(taxAmount);
        feeAmount = Money(feeAmount);
        var totalAmount = Money(roomSubtotal - discountAmount + taxAmount + feeAmount);
        var quoteToken = BookingGuestAccess.GenerateToken();
        var quote = new BookingQuote
        {
            Id = quoteId,
            AccessTokenHash = BookingGuestAccess.Hash(quoteToken),
            AmendmentBookingId = excludedBookingId,
            RoomId = room?.Id,
            RoomTypeId = roomType.Id,
            RoomQuantity = request.RoomQuantity,
            RatePlanId = ratePlan.Id,
            PromotionId = promotion?.Id,
            CheckInDate = checkInDate,
            CheckOutDate = checkOutDate,
            AdultCount = request.AdultCount,
            ChildCount = request.ChildCount,
            Currency = ratePlan.Currency,
            RoomSubtotal = roomSubtotal,
            DiscountAmount = discountAmount,
            IncludedTaxAmount = includedTaxAmount,
            TaxAmount = taxAmount,
            FeeAmount = feeAmount,
            TotalAmount = totalAmount,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(_settings.QuoteLifetimeMinutes),
            Lines = quoteLines
        };
        _db.BookingQuotes.Add(quote);
        if (actorId.HasValue)
        {
            AddAudit(actorId.Value, "AMENDMENT_QUOTE_CREATED", "BookingQuote", quote.Id, null,
                new
                {
                    BookingId = excludedBookingId,
                    quote.RoomTypeId,
                    quote.RoomQuantity,
                    quote.CheckInDate,
                    quote.CheckOutDate,
                    quote.TotalAmount,
                    quote.ExpiresAtUtc
                });
        }
        await _db.SaveChangesAsync(cancellationToken);

        return new PricingQuoteDto(
            quote.Id,
            quoteToken,
            room?.Id,
            roomType.Id,
            roomType.Code,
            roomType.Name,
            request.RoomQuantity,
            ratePlan.Code,
            ratePlan.Name,
            promotion?.Code,
            checkInDate,
            checkOutDate,
            request.AdultCount,
            request.ChildCount,
            nights,
            quote.Currency,
            quote.RoomSubtotal,
            quote.DiscountAmount,
            quote.IncludedTaxAmount,
            quote.TaxAmount,
            quote.FeeAmount,
            quote.TotalAmount,
            quote.ExpiresAtUtc,
            quoteLines.OrderBy(line => line.SortOrder).Select(ToLineDto).ToArray());
    }

    public async Task<ValidatedBookingQuote> ValidateBookingQuoteAsync(
        Guid quoteId,
        string quoteToken,
        CreateBookingRequest booking,
        CancellationToken cancellationToken = default)
    {
        if (quoteId == Guid.Empty || string.IsNullOrWhiteSpace(quoteToken))
            throw new BadRequestException("A valid pricing quote is required.");
        if (quoteToken.Length is < 40 or > 128 || quoteToken.Any(char.IsControl))
            throw new BadRequestException("A valid pricing quote is required.");

        var tokenHash = BookingGuestAccess.Hash(quoteToken.Trim());
        var quote = await _db.BookingQuotes.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == quoteId && item.AccessTokenHash == tokenHash,
                cancellationToken)
            ?? throw new BadRequestException("The pricing quote is invalid.");
        var now = DateTime.UtcNow;
        if (quote.ConsumedAtUtc.HasValue)
            throw new BadRequestException("The pricing quote has already been used.");
        if (quote.ExpiresAtUtc <= now)
            throw new BadRequestException("The pricing quote has expired. Request a new quote.");
        if (quote.AmendmentBookingId.HasValue)
            throw new BadRequestException("An amendment quote cannot create a new reservation.");
        if (quote.RoomId != booking.RoomId ||
            quote.RoomTypeId != (booking.RoomTypeId ?? quote.RoomTypeId) ||
            quote.RoomQuantity != booking.RoomQuantity ||
            quote.CheckInDate != DateOnly.FromDateTime(booking.CheckIn) ||
            quote.CheckOutDate != DateOnly.FromDateTime(booking.CheckOut) ||
            quote.AdultCount != booking.AdultCount ||
            quote.ChildCount != booking.ChildCount)
        {
            throw new BadRequestException(
                "The booking details changed after pricing. Request a new quote.");
        }

        return new ValidatedBookingQuote(
            quote.Id,
            tokenHash,
            quote.RoomId,
            quote.RoomTypeId,
            quote.RoomQuantity,
            quote.Currency,
            quote.RoomSubtotal,
            quote.DiscountAmount,
            quote.IncludedTaxAmount,
            quote.TaxAmount,
            quote.FeeAmount,
            quote.TotalAmount,
            now);
    }

    public async Task<PricingConfigurationDto> GetConfigurationAsync(
        CancellationToken cancellationToken = default) =>
        new(
            await _db.RatePlans.AsNoTracking().OrderBy(plan => plan.Code)
                .Select(plan => new RatePlanDto(
                    plan.Id, plan.Code, plan.Name, plan.Description, plan.Currency,
                    plan.BaseAdjustmentType, plan.BaseAdjustmentValue,
                    plan.MinimumNights, plan.MaximumNights, plan.SellFromDate,
                    plan.SellUntilDate, plan.IsDefault, plan.IsActive, plan.UpdatedAtUtc))
                .ToListAsync(cancellationToken),
            await _db.DailyRoomRates.AsNoTracking()
                .Where(rate => rate.StayDate >= _hotelTime.Today.AddDays(-31))
                .OrderBy(rate => rate.StayDate).ThenBy(rate => rate.RatePlanId)
                .Take(5000)
                .Select(rate => new DailyRateDto(
                    rate.Id, rate.RatePlanId, rate.RoomId, rate.RoomTypeId, rate.RoomCategory,
                    rate.StayDate, rate.Amount, rate.UpdatedAtUtc))
                .ToListAsync(cancellationToken),
            await _db.PricingRules.AsNoTracking().OrderBy(rule => rule.SortOrder)
                .Select(rule => new RuleDto(
                    rule.Id, rule.Code, rule.Name, rule.Kind, rule.Calculation,
                    rule.Value, rule.Currency, rule.IsInclusive,
                    rule.EffectiveFromDate, rule.EffectiveUntilDate,
                    rule.SortOrder, rule.IsActive, rule.UpdatedAtUtc))
                .ToListAsync(cancellationToken),
            await _db.Promotions.AsNoTracking().OrderBy(promotion => promotion.Code)
                .Select(promotion => new PromotionDto(
                    promotion.Id, promotion.Code, promotion.Name,
                    promotion.DiscountType, promotion.Value,
                    promotion.MaximumDiscountAmount, promotion.Currency,
                    promotion.MinimumNights, promotion.RatePlanId,
                    promotion.ValidFromUtc, promotion.ValidUntilUtc,
                    promotion.RedemptionLimit, promotion.RedemptionCount,
                    promotion.IsActive, promotion.UpdatedAtUtc))
                .ToListAsync(cancellationToken));

    public async Task<RatePlanDto> SaveRatePlanAsync(
        Guid? id,
        RatePlanRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        await RequirePricingActorAsync(actorId, cancellationToken);
        if (id == Guid.Empty) throw new NotFoundException("Rate plan not found.");
        var code = NormalizeRequiredCode(request.Code, "rate plan");
        var currency = NormalizeCurrency(request.Currency);
        if (!Enum.IsDefined(request.BaseAdjustmentType))
            throw new BadRequestException("Rate-plan adjustment type is invalid.");
        if (request.MinimumNights is < 1 or > 90 || request.MaximumNights is < 1 or > 90 ||
            request.MinimumNights > request.MaximumNights)
            throw new BadRequestException("Minimum nights cannot exceed maximum nights.");
        if (request.SellFromDate.HasValue && request.SellUntilDate < request.SellFromDate)
            throw new BadRequestException("Rate-plan sell dates are invalid.");
        if (request.BaseAdjustmentValue < 0 ||
            request.BaseAdjustmentValue > 9999999999999999m ||
            request.BaseAdjustmentType == RateAdjustmentType.Percentage &&
            request.BaseAdjustmentValue > 1000 ||
            request.BaseAdjustmentType == RateAdjustmentType.None &&
            request.BaseAdjustmentValue != 0)
            throw new BadRequestException("Rate-plan adjustment is outside the supported range.");

        var entity = id.HasValue
            ? await _db.RatePlans.SingleOrDefaultAsync(plan => plan.Id == id, cancellationToken)
              ?? throw new NotFoundException("Rate plan not found.")
            : new RatePlanEntity { Id = Guid.NewGuid(), CreatedAtUtc = DateTime.UtcNow };
        var oldData = id.HasValue ? JsonSerializer.Serialize(ToRatePlanDto(entity)) : null;
        if (entity.IsDefault && entity.IsActive && (!request.IsDefault || !request.IsActive) &&
            !await _db.RatePlans.AnyAsync(
                plan => plan.Id != entity.Id && plan.IsDefault && plan.IsActive,
                cancellationToken))
        {
            throw new BadRequestException(
                "Assign another active default rate plan before disabling this one.");
        }

        if (request.IsDefault && request.IsActive)
        {
            var replacedDefaults = await _db.RatePlans
                .Where(plan => plan.Id != entity.Id && plan.IsDefault)
                .ToListAsync(cancellationToken);
            foreach (var replacedDefault in replacedDefaults)
            {
                replacedDefault.IsDefault = false;
                replacedDefault.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        entity.Code = code;
        entity.Name = RequireText(request.Name, "Rate-plan name", 120);
        entity.Description = CleanOptional(request.Description, "Rate-plan description", 1000);
        entity.Currency = currency;
        entity.BaseAdjustmentType = request.BaseAdjustmentType;
        entity.BaseAdjustmentValue = request.BaseAdjustmentValue;
        entity.MinimumNights = request.MinimumNights;
        entity.MaximumNights = request.MaximumNights;
        entity.SellFromDate = request.SellFromDate;
        entity.SellUntilDate = request.SellUntilDate;
        entity.IsDefault = request.IsDefault && request.IsActive;
        entity.IsActive = request.IsActive;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        if (!id.HasValue) _db.RatePlans.Add(entity);
        AddAudit(actorId, id.HasValue ? "RATE_PLAN_UPDATED" : "RATE_PLAN_CREATED", "RatePlan", entity.Id, oldData, ToRatePlanDto(entity));
        await SaveConfigurationAsync(cancellationToken);
        return ToRatePlanDto(entity);
    }

    public async Task<DailyRateDto> SaveDailyRateAsync(
        Guid? id,
        DailyRoomRateRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        await RequirePricingActorAsync(actorId, cancellationToken);
        if (id == Guid.Empty) throw new NotFoundException("Daily rate not found.");
        if (request.RatePlanId == Guid.Empty || request.RoomId == Guid.Empty || request.RoomTypeId == Guid.Empty)
            throw new BadRequestException("A daily-rate scope identifier is invalid.");
        if (request.RoomCategory.HasValue && !Enum.IsDefined(request.RoomCategory.Value))
            throw new BadRequestException("Room category is invalid.");
        if ((request.RoomId.HasValue ? 1 : 0) +
            (request.RoomTypeId.HasValue ? 1 : 0) +
            (request.RoomCategory.HasValue ? 1 : 0) != 1)
            throw new BadRequestException("Select exactly one daily-rate scope: room, room type, or room category.");
        if (request.Amount is <= 0 or > 9999999999999999m)
            throw new BadRequestException("Daily rate amount is outside the supported range.");
        if (request.StayDate < _hotelTime.Today.AddDays(-31) ||
            request.StayDate > _hotelTime.Today.AddYears(2))
            throw new BadRequestException("Daily rate date is outside the supported window.");
        if (!await _db.RatePlans.AnyAsync(plan => plan.Id == request.RatePlanId, cancellationToken))
            throw new NotFoundException("Rate plan not found.");
        if (request.RoomId.HasValue &&
            !await _db.Rooms.AnyAsync(room => room.Id == request.RoomId, cancellationToken))
            throw new NotFoundException("Room not found.");
        if (request.RoomTypeId.HasValue &&
            !await _db.RoomTypes.AnyAsync(type => type.Id == request.RoomTypeId, cancellationToken))
            throw new NotFoundException("Room type not found.");

        var entity = id.HasValue
            ? await _db.DailyRoomRates.SingleOrDefaultAsync(rate => rate.Id == id, cancellationToken)
              ?? throw new NotFoundException("Daily rate not found.")
            : new DailyRateEntity { Id = Guid.NewGuid(), CreatedAtUtc = DateTime.UtcNow };
        var oldData = id.HasValue ? JsonSerializer.Serialize(ToDailyRateDto(entity)) : null;
        entity.RatePlanId = request.RatePlanId;
        entity.RoomId = request.RoomId;
        entity.RoomTypeId = request.RoomTypeId;
        entity.RoomCategory = request.RoomCategory;
        entity.StayDate = request.StayDate;
        entity.Amount = Money(request.Amount);
        entity.UpdatedAtUtc = DateTime.UtcNow;
        if (!id.HasValue) _db.DailyRoomRates.Add(entity);
        AddAudit(actorId, id.HasValue ? "DAILY_RATE_UPDATED" : "DAILY_RATE_CREATED", "DailyRoomRate", entity.Id, oldData, ToDailyRateDto(entity));
        await SaveConfigurationAsync(cancellationToken);
        return ToDailyRateDto(entity);
    }

    public async Task DeleteDailyRateAsync(
        Guid id,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        await RequirePricingActorAsync(actorId, cancellationToken);
        if (id == Guid.Empty) throw new NotFoundException("Daily rate not found.");
        var entity = await _db.DailyRoomRates.SingleOrDefaultAsync(rate => rate.Id == id, cancellationToken)
                     ?? throw new NotFoundException("Daily rate not found.");
        var oldData = JsonSerializer.Serialize(ToDailyRateDto(entity));
        _db.DailyRoomRates.Remove(entity);
        AddAudit(actorId, "DAILY_RATE_DELETED", "DailyRoomRate", entity.Id, oldData, null);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<RuleDto> SaveRuleAsync(
        Guid? id,
        PricingRuleRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        await RequirePricingActorAsync(actorId, cancellationToken);
        if (id == Guid.Empty) throw new NotFoundException("Pricing rule not found.");
        var code = NormalizeRequiredCode(request.Code, "pricing rule");
        var currency = NormalizeCurrency(request.Currency);
        if (!Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.Calculation))
            throw new BadRequestException("Pricing-rule kind or calculation is invalid.");
        if (request.Value <= 0 || request.Value > 9999999999999999m ||
            request.Calculation == PricingRuleCalculation.Percentage && request.Value > 1000)
            throw new BadRequestException("Pricing-rule value is outside the supported range.");
        if (request.IsInclusive &&
            (request.Kind != PricingRuleKind.Tax ||
             request.Calculation != PricingRuleCalculation.Percentage))
            throw new BadRequestException("Only percentage taxes can be included in room prices.");
        if (request.EffectiveFromDate.HasValue && request.EffectiveUntilDate < request.EffectiveFromDate)
            throw new BadRequestException("Pricing-rule effective dates are invalid.");
        if (request.SortOrder is < -10000 or > 10000)
            throw new BadRequestException("Pricing-rule sort order is outside the supported range.");

        var entity = id.HasValue
            ? await _db.PricingRules.SingleOrDefaultAsync(rule => rule.Id == id, cancellationToken)
              ?? throw new NotFoundException("Pricing rule not found.")
            : new RuleEntity { Id = Guid.NewGuid(), CreatedAtUtc = DateTime.UtcNow };
        var oldData = id.HasValue ? JsonSerializer.Serialize(ToRuleDto(entity)) : null;
        entity.Code = code;
        entity.Name = RequireText(request.Name, "Pricing-rule name", 120);
        entity.Kind = request.Kind;
        entity.Calculation = request.Calculation;
        entity.Value = request.Value;
        entity.Currency = currency;
        entity.IsInclusive = request.IsInclusive;
        entity.EffectiveFromDate = request.EffectiveFromDate;
        entity.EffectiveUntilDate = request.EffectiveUntilDate;
        entity.SortOrder = request.SortOrder;
        entity.IsActive = request.IsActive;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        if (!id.HasValue) _db.PricingRules.Add(entity);
        AddAudit(actorId, id.HasValue ? "PRICING_RULE_UPDATED" : "PRICING_RULE_CREATED", "PricingRule", entity.Id, oldData, ToRuleDto(entity));
        await SaveConfigurationAsync(cancellationToken);
        return ToRuleDto(entity);
    }

    public async Task<PromotionDto> SavePromotionAsync(
        Guid? id,
        PromotionRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        await RequirePricingActorAsync(actorId, cancellationToken);
        if (id == Guid.Empty) throw new NotFoundException("Promotion not found.");
        var code = NormalizeRequiredCode(request.Code, "promotion");
        var currency = NormalizeCurrency(request.Currency);
        if (!Enum.IsDefined(request.DiscountType))
            throw new BadRequestException("Promotion discount type is invalid.");
        if (request.Value <= 0 || request.Value > 9999999999999999m ||
            request.DiscountType == DiscountType.Percentage && request.Value > 100)
            throw new BadRequestException("Promotion value is outside the supported range.");
        if (request.MaximumDiscountAmount is <= 0 or > 9999999999999999m)
            throw new BadRequestException("Maximum discount must be greater than zero when supplied.");
        var validFromUtc = EnsureUtc(request.ValidFromUtc);
        var validUntilUtc = EnsureUtc(request.ValidUntilUtc);
        if (validUntilUtc <= validFromUtc || validUntilUtc > DateTime.UtcNow.AddYears(5))
            throw new BadRequestException("Promotion validity window is invalid.");
        if (request.MinimumNights is < 1 or > 90)
            throw new BadRequestException("Promotion minimum nights is invalid.");
        if (request.RedemptionLimit is <= 0 or > 100000000)
            throw new BadRequestException("Redemption limit must be greater than zero when supplied.");
        if (request.RatePlanId == Guid.Empty)
            throw new BadRequestException("Rate plan identifier is invalid.");
        if (request.RatePlanId.HasValue &&
            !await _db.RatePlans.AnyAsync(plan => plan.Id == request.RatePlanId, cancellationToken))
            throw new NotFoundException("Rate plan not found.");

        var entity = id.HasValue
            ? await _db.Promotions.SingleOrDefaultAsync(promotion => promotion.Id == id, cancellationToken)
              ?? throw new NotFoundException("Promotion not found.")
            : new PromotionEntity { Id = Guid.NewGuid(), CreatedAtUtc = DateTime.UtcNow };
        var oldData = id.HasValue ? JsonSerializer.Serialize(ToPromotionDto(entity)) : null;
        if (request.RedemptionLimit.HasValue && request.RedemptionLimit < entity.RedemptionCount)
            throw new BadRequestException("Redemption limit cannot be below the existing redemption count.");
        entity.Code = code;
        entity.Name = RequireText(request.Name, "Promotion name", 120);
        entity.DiscountType = request.DiscountType;
        entity.Value = request.Value;
        entity.MaximumDiscountAmount = request.MaximumDiscountAmount.HasValue
            ? Money(request.MaximumDiscountAmount.Value)
            : null;
        entity.Currency = currency;
        entity.MinimumNights = request.MinimumNights;
        entity.RatePlanId = request.RatePlanId;
        entity.ValidFromUtc = validFromUtc;
        entity.ValidUntilUtc = validUntilUtc;
        entity.RedemptionLimit = request.RedemptionLimit;
        entity.IsActive = request.IsActive;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        if (!id.HasValue) _db.Promotions.Add(entity);
        AddAudit(actorId, id.HasValue ? "PROMOTION_UPDATED" : "PROMOTION_CREATED", "Promotion", entity.Id, oldData, ToPromotionDto(entity));
        await SaveConfigurationAsync(cancellationToken);
        return ToPromotionDto(entity);
    }

    public async Task<int> DeleteExpiredUnconsumedQuotesAsync(
        DateTime utcNow,
        int batchSize = 500,
        CancellationToken cancellationToken = default)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The quote cleanup clock must be UTC.", nameof(utcNow));
        batchSize = Math.Clamp(batchSize, 1, 1000);
        var deleteBeforeUtc = utcNow.AddDays(-1);
        var ids = await _db.BookingQuotes
            .Where(quote =>
                quote.ConsumedAtUtc == null &&
                quote.ExpiresAtUtc <= deleteBeforeUtc)
            .OrderBy(quote => quote.ExpiresAtUtc)
            .ThenBy(quote => quote.Id)
            .Select(quote => quote.Id)
            .Take(batchSize)
            .ToArrayAsync(cancellationToken);
        if (ids.Length == 0) return 0;
        return await _db.BookingQuotes
            .Where(quote => ids.Contains(quote.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private void ValidateStay(
        DateOnly checkInDate,
        DateOnly checkOutDate,
        int adultCount,
        int childCount)
    {
        if (adultCount is < 1 or > 20 || childCount is < 0 or > 20)
            throw new BadRequestException("A quote requires 1-20 adults and 0-20 children.");
        if (checkInDate < _hotelTime.Today)
            throw new BadRequestException("Check-in cannot be in the past.");
        var nights = checkOutDate.DayNumber - checkInDate.DayNumber;
        if (nights is < 1 or > 90)
            throw new BadRequestException("A reservation must contain between 1 and 90 nights.");
        if (checkInDate > _hotelTime.Today.AddYears(2))
            throw new BadRequestException("Reservations cannot be created more than two years in advance.");
    }

    private static decimal ApplyRatePlanAdjustment(
        decimal basePrice,
        RatePlanEntity plan) => Money(plan.BaseAdjustmentType switch
        {
            RateAdjustmentType.None => basePrice,
            RateAdjustmentType.Percentage => basePrice * (1m + plan.BaseAdjustmentValue / 100m),
            RateAdjustmentType.FixedAmount => basePrice + plan.BaseAdjustmentValue,
            _ => throw new InvalidOperationException("Unsupported rate-plan adjustment.")
        });

    private async Task SaveConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException(
                "The pricing configuration conflicts with an existing code or daily rate.");
        }
    }

    private async Task RequirePricingActorAsync(Guid actorId, CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty)
            throw new UnauthorizedAccessException("The pricing actor is invalid.");
        var allowed = await _db.Users.AsNoTracking().AnyAsync(user =>
            user.Id == actorId && user.Status == ProfileStatus.Active &&
            (user.Role == UserRole.Admin || user.Role == UserRole.Manager),
            cancellationToken);
        if (!allowed)
            throw new UnauthorizedAccessException("An active administrator or manager is required.");
    }

    private async Task RequireReservationActorAsync(Guid actorId, CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty)
            throw new UnauthorizedAccessException("The reservation actor is invalid.");
        var actor = await _db.Users.AsNoTracking().SingleOrDefaultAsync(user =>
            user.Id == actorId && user.Status == ProfileStatus.Active,
            cancellationToken);
        if (actor is null || actor.Role == UserRole.Client ||
            actor.Role == UserRole.Staff && actor.Department is not ("Reception" or "FrontDesk"))
        {
            throw new UnauthorizedAccessException("The actor cannot create amendment quotes.");
        }
    }

    private void AddAudit(
        Guid actorId,
        string action,
        string entityType,
        Guid entityId,
        string? oldData,
        object? newData) => _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ProfileId = actorId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId.ToString(),
            OldDataJson = oldData,
            NewDataJson = newData is null ? null : JsonSerializer.Serialize(newData),
            CreatedAt = DateTime.UtcNow
        });

    private static PricingQuoteLineDto ToLineDto(BookingQuoteLine line) => new(
        line.Type,
        line.Code,
        line.Description,
        line.StayDate,
        line.Quantity,
        line.UnitAmount,
        line.Amount,
        line.IsInclusive);

    private static RatePlanDto ToRatePlanDto(RatePlanEntity plan) => new(
        plan.Id, plan.Code, plan.Name, plan.Description, plan.Currency,
        plan.BaseAdjustmentType, plan.BaseAdjustmentValue, plan.MinimumNights,
        plan.MaximumNights, plan.SellFromDate, plan.SellUntilDate,
        plan.IsDefault, plan.IsActive, plan.UpdatedAtUtc);

    private static DailyRateDto ToDailyRateDto(DailyRateEntity rate) => new(
        rate.Id, rate.RatePlanId, rate.RoomId, rate.RoomTypeId, rate.RoomCategory,
        rate.StayDate, rate.Amount, rate.UpdatedAtUtc);

    private static RuleDto ToRuleDto(RuleEntity rule) => new(
        rule.Id, rule.Code, rule.Name, rule.Kind, rule.Calculation, rule.Value,
        rule.Currency, rule.IsInclusive, rule.EffectiveFromDate,
        rule.EffectiveUntilDate, rule.SortOrder, rule.IsActive, rule.UpdatedAtUtc);

    private static PromotionDto ToPromotionDto(PromotionEntity promotion) => new(
        promotion.Id, promotion.Code, promotion.Name, promotion.DiscountType,
        promotion.Value, promotion.MaximumDiscountAmount, promotion.Currency,
        promotion.MinimumNights, promotion.RatePlanId, promotion.ValidFromUtc,
        promotion.ValidUntilUtc, promotion.RedemptionLimit,
        promotion.RedemptionCount, promotion.IsActive, promotion.UpdatedAtUtc);

    private static string NormalizeRequiredCode(string value, string field) =>
        NormalizeOptionalCode(value, field, field == "promotion" ? 40 : 30)
        ?? throw new BadRequestException($"A {field} code is required.");

    private static string? NormalizeOptionalCode(string? value, string field, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length > maximumLength || normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '_')))
            throw new BadRequestException($"The {field} code contains unsupported characters.");
        return normalized;
    }

    private static string NormalizeCurrency(string value)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.Length != 3 || normalized.Any(character => character is < 'A' or > 'Z'))
            throw new BadRequestException("Currency must be an uppercase ISO 4217 code.");
        return normalized;
    }

    private static string RequireText(string value, string field, int maximumLength)
    {
        var cleaned = value?.Trim() ?? string.Empty;
        if (cleaned.Length == 0 || cleaned.Length > maximumLength || cleaned.Any(char.IsControl))
            throw new BadRequestException($"{field} is required and cannot exceed {maximumLength} characters.");
        return cleaned;
    }

    private static string? CleanOptional(string? value, string field, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = value.Trim();
        if (cleaned.Length > maximumLength || cleaned.Any(char.IsControl))
            throw new BadRequestException($"{field} is invalid or too long.");
        return cleaned;
    }

    private static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static decimal Money(decimal amount)
    {
        var rounded = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (rounded is < -9999999999999999m or > 9999999999999999m)
            throw new BadRequestException("Calculated pricing is outside the supported monetary range.");
        return rounded;
    }
}
