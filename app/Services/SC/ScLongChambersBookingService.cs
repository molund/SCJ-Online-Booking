using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using SCJ.Booking.Data;
using SCJ.Booking.Data.Constants;
using SCJ.Booking.MVC.Utils;
using SCJ.Booking.MVC.ViewModels.SC;
using SCJ.OnlineBooking;

namespace SCJ.Booking.MVC.Services.SC
{
    public class ScLongChambersBookingService
        : ScLotteryEnabledBookingServiceBase,
            IScLotteryEnabledBookingService
    {
        public ScLongChambersBookingService(
            ApplicationDbContext dbContext,
            IConfiguration configuration,
            SessionService sessionService,
            IViewRenderService viewRenderService,
            ScCacheService scCacheService
        )
            : base(dbContext, configuration, sessionService, viewRenderService, scCacheService) { }

        /// <summary>
        ///     Books the long chambers hearing Regular bookings
        ///     Saves the lottery entry for Fair-Use booking
        /// </summary>
        public async Task<ScCaseConfirmViewModel> CreateBookingAsync(
            ScCaseConfirmViewModel model,
            ClaimsPrincipal user
        )
        {
            //if the user could not be detected return
            if (user == null)
            {
                return model;
            }

            ScSessionBookingInfo bookingInfo = _session.ScBookingInfo;

            model.IsBooked = false;
            bookingInfo.IsBooked = false;

            string userDisplayName = OpenIdConnectHelper.GetUserFullName(user);
            long userId = long.Parse(user.FindFirst(ClaimTypes.Sid)?.Value ?? "0");

            // store user info in session for next booking
            var userInfo = new SessionUserInfo
            {
                Phone = model.Phone,
                Email = model.EmailAddress,
                ContactName = $"{userDisplayName}"
            };
            _session.UserInfo = userInfo;

            // generate a trial booking id (for troubleshooting between SCSS and SCJOB)
            var lotteryEntryId = GenerateLotteryEntryId() + "-" + userId;

            if (bookingInfo.FormulaType == ScFormulaType.FairUseBooking)
            {
                if (await CheckIfTrialAlreadyRequestedAsync())
                {
                    bookingInfo.ApiBookingResultMessage =
                        "A chambers hearing has already been requested for this case.";
                }
                else
                {
                    await _dbWriterService.SaveBookingHistory(
                        userId,
                        "SC",
                        bookingInfo.BookingLocationName,
                        bookingInfo.ChambersHearingSubTypeId,
                        ScFormulaType.FairUseBooking
                    );

                    bookingInfo.LotteryEntryId = lotteryEntryId;
                    _session.ScBookingInfo = bookingInfo;
                    await _dbWriterService.SaveLotteryEntry(userId, bookingInfo, userInfo);

                    //update model
                    model.IsBooked = true;
                    bookingInfo.IsBooked = true;

                    // send email
                    string emailBody = await GetEmailBodyAsync();
                    string fileNumber = bookingInfo.FullCaseNumber;
                    string emailSubject = $"Chambers hearing booking request for {fileNumber}";

                    await _mailService.QueueEmailAsync(
                        "SC",
                        model.EmailAddress,
                        emailSubject,
                        emailBody
                    );
                }
            }
            else if (bookingInfo.FormulaType == ScFormulaType.RegularBooking)
            {
                // Available dates
                (List<DateTime> availableTrialDates, _) = await GetAvailableBookingDatesAsync(
                    ScFormulaType.RegularBooking,
                    bookingInfo.RegularFormula
                );

                // check if selected date exists in the available dates
                bool dateAvailable =
                    bookingInfo.SelectedRegularDate.HasValue
                    && availableTrialDates.Contains(bookingInfo.SelectedRegularDate.Value);

                // throw an exception if the date is no longer available
                if (!dateAvailable)
                {
                    throw new InvalidOperationException(
                        "The date you selected is no longer available."
                    );
                }

                BookingHearingResult result = new BookingHearingResult();

                // book chammbers in API
                BookingSCCHHearingInfo requestPayload =
                    new()
                    {
                        BookingLocationID = bookingInfo.RegularFormula.BookingLocationID,
                        CEIS_Physical_File_ID = bookingInfo.PhysicalFileId,
                        CourtClass = bookingInfo.SelectedCourtFile.courtClassCode,
                        FormulaType = ScFormulaType.RegularBooking,
                        HearingLength = bookingInfo.BookingLength.GetValueOrDefault(1),
                        LocationID = bookingInfo.AlternateLocationRegistryId,
                        RequestedBy = $"{userDisplayName} {model.Phone} {model.EmailAddress}",
                        HearingDate = bookingInfo.SelectedRegularDate.Value,
                        HearingTypeId = bookingInfo.ChambersHearingSubTypeId.Value,
                        // @TODO: Contact Lorne about this
                        //SCJOB_Trial_Booking_ID = lotteryEntryId,
                        //SCJOB_Trial_Booking_Date = DateTime.Now
                    };

                _logger.Information("BOOKING SUPREME COURT => scCHBookHearingAsync()");
                _logger.Information(JsonSerializer.Serialize(requestPayload));

                result = await _client.scCHBookHearingAsync(requestPayload);

                //get the raw result
                bookingInfo.ApiBookingResultMessage = result.bookingResult;

                //test to see if the booking was successful
                if (result.bookingResult.ToLower().StartsWith("success"))
                {
                    bookingInfo.LotteryEntryId = lotteryEntryId;
                    _session.ScBookingInfo = bookingInfo;

                    //create database entry
                    await _dbWriterService.SaveBookingHistory(
                        userId,
                        "SC",
                        bookingInfo.BookingLocationName,
                        bookingInfo.ChambersHearingSubTypeId,
                        ScFormulaType.RegularBooking
                    );

                    // update model
                    model.IsBooked = true;
                    bookingInfo.IsBooked = true;

                    // send email
                    string emailBody = await GetEmailBodyAsync();
                    string fileNumber = bookingInfo.FullCaseNumber;
                    string startDate = bookingInfo.SelectedRegularDate?.ToString("MMMM d, yyyy");
                    string emailSubject =
                        $"Chambers hearing booking for {fileNumber} on {startDate}";

                    await _mailService.QueueEmailAsync(
                        "SC",
                        model.EmailAddress,
                        emailSubject,
                        emailBody
                    );

                    //clear booking info session
                    _session.ScBookingInfo = null;
                }
                else
                {
                    _logger.Information($"API Response: {result.bookingResult}");
                    model.IsBooked = false;
                    bookingInfo.IsBooked = false;
                }
            }

            // save the booking info back to the session
            _session.ScBookingInfo = bookingInfo;

            return model;
        }

        public new async Task<bool> CheckIfLongChambersAlreadyRequestedAsync()
        {
            return await CheckIfBookingAlreadyRequestedAsync(ScHearingType.LONG_CHAMBERS);
        }
    }
}
